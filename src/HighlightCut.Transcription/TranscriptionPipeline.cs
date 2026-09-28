using System.Buffers.Binary;
using HighlightCut.Core.Transcripts;

namespace HighlightCut.Transcription;

/// <summary>Recognizes speech in short pieces of 16 kHz mono audio.</summary>
public interface ISpeechRecognizer : IDisposable
{
    /// <summary>The words in <paramref name="samples"/>, timed on the source timeline (the piece starts at <paramref name="offset"/>).</summary>
    IReadOnlyList<Word> Recognize(float[] samples, double offset);

    /// <summary>Some word times were estimated from the audio because the model gave none.</summary>
    bool HasApproximateTimes => false;

    /// <summary>How many pieces <see cref="Recognize"/> may be given at once, from different threads.</summary>
    int Parallelism => 1;
}

/// <summary>
/// Reads 16 kHz mono float audio as it is decoded, cuts it into pieces of at most half a minute at the quietest
/// moment near the end of each (so words are not cut in half) and recognizes them, as many at once as the recognizer
/// takes, on threads below normal priority. The words come out in order. Memory stays small however long the
/// recording is.
/// </summary>
public static class TranscriptionPipeline
{
    public const int SampleRate = 16000;

    /// <summary>Longest piece given to the recognizer, in seconds (Whisper takes at most 30 s).</summary>
    public const double MaxPiece = 28;

    /// <summary>A piece is cut at the quietest moment after this many seconds.</summary>
    public const double MinPiece = 18;

    /// <param name="audio">Raw little-endian 32-bit float samples, 16 kHz mono (ffmpeg <c>-f f32le</c>).</param>
    /// <param name="duration">Length of the audio in seconds, for progress.</param>
    /// <param name="onPiece">Called after each piece, in order, with its words and the progress 0..1.</param>
    public static async Task<List<Word>> RunAsync(Stream audio, double duration, ISpeechRecognizer recognizer,
        Action<IReadOnlyList<Word>, double>? onPiece = null, CancellationToken cancellationToken = default)
    {
        int max = (int)(MaxPiece * SampleRate), min = (int)(MinPiece * SampleRate);
        var buffer = new float[max];
        int filled = 0;
        long consumed = 0;
        var words = new List<Word>();
        var bytes = new byte[64 * 1024];
        int carry = 0;
        int parallelism = Math.Max(1, recognizer.Parallelism);
        using var workers = new Workers(parallelism);
        // Pieces being recognized, oldest first, with where each ends.
        var pending = new Queue<(Task<IReadOnlyList<Word>> Words, long End)>();

        void Start(int count)
        {
            var piece = buffer[..count];
            double offset = (double)consumed / SampleRate;
            consumed += count;
            pending.Enqueue((workers.Run(() => recognizer.Recognize(piece, offset)), consumed));
            Array.Copy(buffer, count, buffer, 0, filled - count);
            filled -= count;
        }

        // Hands on the oldest pieces' words until at most `keep` are left.
        async Task Finish(int keep)
        {
            while (pending.Count > keep)
            {
                var (task, end) = pending.Peek();
                var found = await task.ConfigureAwait(false);
                pending.Dequeue();
                words.AddRange(found);
                onPiece?.Invoke(found, duration > 0 ? Math.Min(1, end / (duration * SampleRate)) : 0);
            }
        }

        try
        {
            while (true)
            {
                int read = await audio.ReadAsync(bytes.AsMemory(carry), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                int available = carry + read;
                int whole = available / 4 * 4;
                for (int off = 0; off < whole; off += 4)
                {
                    buffer[filled++] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(off, 4));
                    if (filled == max)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Start(QuietestCut(buffer, min, max));
                        // A few pieces wait ahead, so a thread that finishes one takes the next straight away.
                        await Finish(2 * parallelism - 1).ConfigureAwait(false);
                    }
                }
                carry = available - whole;
                if (carry > 0)
                    Buffer.BlockCopy(bytes, whole, bytes, 0, carry);
            }
            if (filled > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Start(filled);
            }
            await Finish(0).ConfigureAwait(false);
        }
        finally
        {
            // Stopped or failed: the recognizer is disposed after this returns, so wait for the pieces it is still on.
            foreach (var (task, _) in pending)
                await task.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                    .ConfigureAwait(false);
        }
        return words;
    }

    /// <summary>
    /// Threads below normal priority that recognize pieces, so playback and the UI keep the CPU they need. Each takes
    /// the next piece when it is done with one.
    /// </summary>
    private sealed class Workers : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();

        public Workers(int count)
        {
            for (int i = 0; i < count; i++)
                new Thread(Work) { IsBackground = true, Name = "HighlightCut transcription" }.Start();
        }

        public Task<T> Run<T>(Func<T> work)
        {
            // Continuations run on the pipeline's thread pool, not on a worker that would then wait for the next piece.
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    done.SetResult(work());
                }
                catch (Exception e)
                {
                    done.SetException(e);
                }
            });
            return done.Task;
        }

        private void Work()
        {
            LowPriority.LowerCurrentThread();
            foreach (var work in _queue.GetConsumingEnumerable())
                work();
        }

        /// <summary>The threads end once they have run what they were given.</summary>
        public void Dispose() => _queue.CompleteAdding();
    }

    /// <summary>
    /// Index in [<paramref name="from"/>, <paramref name="to"/>) at the middle of the quietest 100 ms: the best place
    /// to end a piece.
    /// </summary>
    public static int QuietestCut(ReadOnlySpan<float> samples, int from, int to)
    {
        const int Window = SampleRate / 10, Step = SampleRate / 100;
        int best = to;
        double quietest = double.MaxValue;
        for (int start = from; start + Window <= to; start += Step)
        {
            double energy = 0;
            for (int i = start; i < start + Window; i++)
                energy += samples[i] * samples[i];
            if (energy < quietest)
            {
                quietest = energy;
                best = start + Window / 2;
            }
        }
        return best;
    }
}
