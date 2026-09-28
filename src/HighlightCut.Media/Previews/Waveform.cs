using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.ExceptionServices;
using HighlightCut.Media.Probing;
using HighlightCut.Media.Tools;

namespace HighlightCut.Media.Previews;

/// <summary>
/// Audio peaks of every audio stream, 100 buckets per second, filled progressively while ffmpeg decodes, in one
/// stretch from the start or in several at once (<see cref="WaveformExtractor"/> splits long files). Reading while it
/// fills is safe: a stretch only grows.
/// </summary>
public sealed class WaveformData
{
    public const int BucketsPerSecond = 100;

    private readonly float[][] _peaks;

    /// <summary>What is decoded: (first bucket, buckets filled) per stretch, in order. Replaced, never changed.</summary>
    private (int Start, int Count)[] _stretches = [(0, 0)];

    public WaveformData(int streams, double duration)
    {
        int buckets = (int)Math.Ceiling(Math.Max(0, duration) * BucketsPerSecond) + 1;
        _peaks = [.. Enumerable.Range(0, streams).Select(_ => new float[buckets])];
    }

    internal WaveformData(float[][] peaks, int filled)
    {
        _peaks = peaks;
        _stretches = [(0, filled)];
    }

    public int StreamCount => _peaks.Length;
    public int Capacity => _peaks.Length == 0 ? 0 : _peaks[0].Length;

    /// <summary>Buckets decoded from the start without a gap (silence detection reads these).</summary>
    public int Filled
    {
        get
        {
            int end = 0;
            foreach (var (start, count) in Volatile.Read(ref _stretches))
            {
                if (start > end)
                    break;
                end = Math.Max(end, start + count);
            }
            return end;
        }
    }

    /// <summary>Buckets decoded anywhere, for progress.</summary>
    public int Decoded => Volatile.Read(ref _stretches).Sum(s => s.Count);

    public bool IsComplete { get; internal set; }

    /// <summary>Raw peak (linear, 0..1) of one bucket.</summary>
    public float this[int stream, int bucket] => _peaks[stream][bucket];

    internal float[][] RawPeaks => _peaks;

    internal void Set(int stream, int bucket, float peak)
    {
        if (bucket < _peaks[stream].Length)
            _peaks[stream][bucket] = peak;
    }

    internal void Publish(int filled) => Publish([(0, filled)]);

    /// <summary>The stretches decoded so far, in order, each (first bucket, buckets filled).</summary>
    internal void Publish(IEnumerable<(int Start, int Count)> stretches) =>
        Volatile.Write(ref _stretches, [.. stretches.Select(s => (s.Start, Math.Clamp(s.Count, 0, Math.Max(0, Capacity - s.Start))))]);

    /// <summary>Takes over the peaks of another waveform of the same file, e.g. one read from the cache.</summary>
    public void CopyFrom(WaveformData other)
    {
        ArgumentNullException.ThrowIfNull(other);
        int n = Math.Min(Capacity, other.Capacity);
        for (int s = 0; s < Math.Min(StreamCount, other.StreamCount); s++)
            Array.Copy(other._peaks[s], _peaks[s], n);
        Publish(Math.Min(other.Filled, n));
        IsComplete = other.IsComplete;
    }

    /// <summary>
    /// Display height 0..1 of the loudest bucket between two times, on a dB scale (−48 dB → 0) so quiet
    /// speech is still visible.
    /// </summary>
    public double Peak(int stream, double start, double end)
    {
        if (stream < 0 || stream >= _peaks.Length)
            return 0;
        int b0 = Math.Max(0, (int)Math.Floor(start * BucketsPerSecond));
        int b1 = Math.Max(b0 + 1, (int)Math.Ceiling(end * BucketsPerSecond));
        float max = 0;
        var p = _peaks[stream];
        foreach (var (first, count) in Volatile.Read(ref _stretches))
        {
            for (int b = Math.Max(b0, first), last = Math.Min(b1, first + count); b < last; b++)
                max = Math.Max(max, p[b]);
        }
        return ToDisplay(max);
    }

    public static double ToDisplay(double linear) =>
        linear <= 0 ? 0 : Math.Clamp(1 + 20 * Math.Log10(linear) / 48, 0, 1);

    /// <summary>A display height (<see cref="ToDisplay"/>) as it looks with the track's volume changed by <paramref name="gainDb"/>.</summary>
    public static double WithGain(double display, double gainDb) =>
        display <= 0 || gainDb == 0 ? display
        : gainDb <= HighlightCut.Core.Model.TrackMix.MinGainDb ? 0
        : Math.Clamp(display + gainDb / 48, 0, 1);
}

/// <summary>
/// Decodes all audio streams (each resampled to 8 kHz mono and merged into one interleaved stream) and folds the
/// samples into <see cref="WaveformData"/> peaks. Audio decodes on one core per ffmpeg, so a long file is split into
/// stretches decoded at once, which fill in side by side.
/// </summary>
public static class WaveformExtractor
{
    public const int SampleRate = 8000;
    private const int SamplesPerBucket = SampleRate / WaveformData.BucketsPerSecond;

    /// <summary>Shortest stretch worth its own ffmpeg, in seconds.</summary>
    private const double MinStretch = 30;

    private const int MaxStretches = 8;

    /// <summary>
    /// A seek into compressed audio can land a few milliseconds off (ffmpeg 9 lands AAC 14 ms late), so a stretch may
    /// end a bucket or two short. Up to this many missing buckets still count as the whole stretch decoded; the peaks
    /// cannot show the difference, and silence detection must not stop at the gap.
    /// </summary>
    private const int SeekSlack = 10;

    public static WaveformData Create(MediaInfo info) => new(info.Audio.Length, info.Duration);

    /// <summary>How many stretches a file of <paramref name="duration"/> seconds is decoded in on <paramref name="cores"/> cores.</summary>
    public static int StretchesFor(double duration, int cores) =>
        Math.Clamp((int)(duration / MinStretch), 1, Math.Clamp(cores, 1, MaxStretches));

    public static Task ExtractAsync(MediaInfo info, WaveformData target, Action? onChunk = null,
        CancellationToken cancellationToken = default) =>
        ExtractAsync(info, target, StretchesFor(info.Duration, Environment.ProcessorCount), onChunk, cancellationToken);

    internal static async Task ExtractAsync(MediaInfo info, WaveformData target, int parts, Action? onChunk,
        CancellationToken cancellationToken)
    {
        if (info.Audio.Length == 0)
        {
            target.IsComplete = true;
            return;
        }
        int size = (int)Math.Ceiling((double)target.Capacity / parts);
        var filled = new int[parts];
        var gate = new Lock();
        void Report(int part, int count)
        {
            lock (gate)
            {
                filled[part] = count;
                target.Publish(filled.Select((c, p) => (p * size, c)));
            }
            onChunk?.Invoke();
        }

        // One failing stretch stops the others; its error is the one reported.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = Enumerable.Range(0, parts).Select(async part =>
        {
            try
            {
                await ExtractStretchAsync(info, target, part * size, part == parts - 1 ? null : size, count => Report(part, count), stop.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }).ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                 && tasks.FirstOrDefault(t => t.IsFaulted)?.Exception?.InnerException is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
        target.IsComplete = true;
        onChunk?.Invoke();
    }

    /// <summary>Decodes <paramref name="buckets"/> buckets (to the end if null) into the waveform from <paramref name="first"/> on.</summary>
    private static Task ExtractStretchAsync(MediaInfo info, WaveformData target, int first, int? buckets, Action<int> report,
        CancellationToken cancellationToken)
    {
        int n = info.Audio.Length;
        return ToolProcess.RunAsync("ffmpeg", Arguments(info, (double)first / WaveformData.BucketsPerSecond,
            buckets is { } b ? (double)b / WaveformData.BucketsPerSecond : null), async (stdout, ct) =>
        {
            var buffer = new byte[64 * 1024];
            int frameBytes = 2 * n, carry = 0, sampleInBucket = 0, bucket = 0;
            var peaks = new float[n];
            var lastPublish = DateTime.UtcNow;
            while (true)
            {
                int read = await stdout.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                int available = carry + read;
                int whole = available / frameBytes * frameBytes;
                for (int off = 0; off < whole; off += frameBytes)
                {
                    for (int s = 0; s < n; s++)
                    {
                        short v = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(off + 2 * s, 2));
                        float a = Math.Abs(v / 32768f);
                        if (a > peaks[s])
                            peaks[s] = a;
                    }
                    if (++sampleInBucket == SamplesPerBucket)
                    {
                        for (int s = 0; s < n; s++)
                        {
                            target.Set(s, first + bucket, peaks[s]);
                            peaks[s] = 0;
                        }
                        bucket++;
                        sampleInBucket = 0;
                    }
                }
                carry = available - whole;
                if (carry > 0)
                    Buffer.BlockCopy(buffer, whole, buffer, 0, carry);
                if ((DateTime.UtcNow - lastPublish).TotalMilliseconds > 250)
                {
                    report(bucket);
                    lastPublish = DateTime.UtcNow;
                }
            }
            if (sampleInBucket > 0)
            {
                for (int s = 0; s < n; s++)
                    target.Set(s, first + bucket, peaks[s]);
                bucket++;
            }
            report(buckets is { } limit ? (bucket >= limit - SeekSlack ? limit : bucket) : bucket);
        }, cancellationToken);
    }

    /// <summary>ffmpeg arguments for the whole file: one 8 kHz mono channel per audio stream, interleaved s16le on stdout.</summary>
    public static IReadOnlyList<string> Arguments(MediaInfo info) => Arguments(info, 0, null);

    /// <summary>The same for <paramref name="length"/> seconds (to the end if null) from <paramref name="start"/>.</summary>
    public static IReadOnlyList<string> Arguments(MediaInfo info, double start, double? length)
    {
        var args = new List<string> { "-v", "error" };
        if (start > 0)
            args.AddRange(["-ss", start.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", info.Path, "-vn", "-sn", "-dn"]);
        if (length is { } seconds)
            args.AddRange(["-t", seconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        const string Mono = "aresample={0},aformat=sample_fmts=s16:channel_layouts=mono";
        string mono = string.Format(CultureInfo.InvariantCulture, Mono, SampleRate);
        if (info.Audio.Length == 1)
        {
            args.AddRange(["-map", $"0:{info.Audio[0].Index}", "-af", mono]);
        }
        else
        {
            var graph = string.Join(';', info.Audio.Select((a, i) => $"[0:{a.Index}]{mono}[m{i}]"))
                + ";" + string.Concat(info.Audio.Select((_, i) => $"[m{i}]"))
                + $"amerge=inputs={info.Audio.Length}[out]";
            args.AddRange(["-filter_complex", graph, "-map", "[out]"]);
        }
        args.AddRange(["-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"]);
        return args;
    }
}
