using System.Diagnostics;
using HighlightCut.Core.Transcripts;
using HighlightCut.Transcription.Models;

namespace HighlightCut.Transcription.Tests;

public class WordBuilderTests
{
    [Fact]
    public void Tokens_become_words_with_punctuation_attached()
    {
        string[] tokens = [" A", "sk", " not", ",", " co", "un", "try", "."];
        float[] starts = [0.0f, 0.24f, 0.40f, 0.60f, 0.96f, 1.04f, 1.12f, 1.60f];

        var words = WordBuilder.Build(tokens, starts, offset: 10, end: 12);

        Assert.Equal(["Ask", "not,", "country."], words.Select(w => w.Text));
        Assert.Equal([10.0, 10.4, 10.96], words.Select(w => Math.Round(w.Start, 2)));
        Assert.Equal(10.4, words[0].End, 2);
        // The last word ends a little after its last token, within the piece.
        Assert.Equal(11.9, words[2].End, 2);
    }

    [Fact]
    public void A_word_before_a_long_pause_does_not_stretch_over_it()
    {
        var words = WordBuilder.Build(["▁um", "▁so"], [0f, 5f], 0, 6);
        Assert.Equal("um", words[0].Text);
        Assert.InRange(words[0].End, 0.2, 0.5);
    }
}

public class EstimatedTimesTests
{
    [Fact]
    public void Words_without_times_are_spread_over_the_speech()
    {
        // 1 s silence, 2 s of tone, 1 s silence, 1 s of tone.
        var samples = Enumerable.Range(0, 16000 * 5).Select(i =>
        {
            double t = i / 16000.0;
            return t is >= 1 and < 3 or >= 4 ? (float)(0.3 * Math.Sin(2 * Math.PI * 200 * t)) : 0f;
        }).ToArray();

        var words = WordBuilder.Estimate(["one", "two", "three"], samples, offset: 100);

        Assert.Equal(["one", "two", "three"], words.Select(w => w.Text));
        Assert.Equal(101, words[0].Start, 1);
        Assert.Equal(105, words[2].End, 1);
        // The gap between the stretches of speech is skipped, not given to a word.
        Assert.True(words[2].Start >= 102.2);
    }
}

public class TranscriptTests
{
    [Fact]
    public void Phrases_end_at_sentences_and_long_pauses()
    {
        var t = new Transcript("m", "auto",
        [
            new("Hello", 0, 0.4), new("there.", 0.5, 0.9), new("So", 1.0, 1.2), new("um", 1.3, 1.5),
            new("next", 3.0, 3.3), new("part", 3.4, 3.8),
        ]);

        var phrases = t.Phrases();

        Assert.Equal(["Hello there.", "So um", "next part"], phrases.Select(p => p.Text));
        Assert.Equal((1.0, 1.5, 2, 2), (phrases[1].Start, phrases[1].End, phrases[1].FirstWord, phrases[1].WordCount));
        Assert.Equal(["um", "next"], t.Between(1.4, 3.1).Select(w => w.Text));
    }
}

public class TranscriptionPipelineTests
{
    /// <summary>Records the pieces it is given and says one word per piece.</summary>
    private sealed class FakeRecognizer : ISpeechRecognizer
    {
        public List<(double Offset, double Length)> Pieces { get; } = [];

        public IReadOnlyList<Word> Recognize(float[] samples, double offset)
        {
            Pieces.Add((offset, (double)samples.Length / TranscriptionPipeline.SampleRate));
            return [new Word($"piece{Pieces.Count}", offset, offset + 0.5)];
        }

        public void Dispose()
        {
        }
    }

    /// <summary>70 s of tone with silent gaps at 21–21.5 s and 44–44.5 s.</summary>
    private static MemoryStream Audio()
    {
        const int Rate = TranscriptionPipeline.SampleRate;
        var ms = new MemoryStream();
        var bytes = new byte[4];
        for (int i = 0; i < 70 * Rate; i++)
        {
            double t = (double)i / Rate;
            bool silent = t is >= 21 and < 21.5 or >= 44 and < 44.5;
            float v = silent ? 0 : (float)(0.3 * Math.Sin(2 * Math.PI * 220 * t));
            BitConverter.TryWriteBytes(bytes, v);
            ms.Write(bytes);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Long_audio_is_cut_into_pieces_at_its_pauses()
    {
        var recognizer = new FakeRecognizer();
        var progress = new List<double>();

        var words = await TranscriptionPipeline.RunAsync(Audio(), 70, recognizer, (_, p) => progress.Add(p), TestContext.Current.CancellationToken);

        // 70 s in pieces of at most 28 s: cut in the gaps at 21 s and 44 s, then the rest.
        Assert.Equal(3, recognizer.Pieces.Count);
        Assert.All(recognizer.Pieces, p => Assert.True(p.Length <= TranscriptionPipeline.MaxPiece));
        Assert.InRange(recognizer.Pieces[0].Length, 21, 21.5);
        Assert.InRange(recognizer.Pieces[1].Offset + recognizer.Pieces[1].Length, 44, 44.5);
        for (int i = 1; i < recognizer.Pieces.Count; i++)
            Assert.Equal(recognizer.Pieces[i - 1].Offset + recognizer.Pieces[i - 1].Length, recognizer.Pieces[i].Offset, 6);
        Assert.Equal(70, recognizer.Pieces[^1].Offset + recognizer.Pieces[^1].Length, 3);
        Assert.Equal(["piece1", "piece2", "piece3"], words.Select(w => w.Text));
        Assert.Equal(1, progress[^1], 3);
    }

    /// <summary>Takes pieces from several threads at once, the later ones faster, and says one word per piece.</summary>
    private sealed class ParallelRecognizer(int parallelism) : ISpeechRecognizer
    {
        private int _running;

        public int Parallelism => parallelism;
        public int MostAtOnce { get; private set; }
        public bool LowPriority { get; private set; } = true;
        public int Running => Volatile.Read(ref _running);

        /// <summary>Held until set; stands for a slow model.</summary>
        public ManualResetEventSlim Go { get; } = new(true);

        public IReadOnlyList<Word> Recognize(float[] samples, double offset)
        {
            int now = Interlocked.Increment(ref _running);
            lock (this)
            {
                MostAtOnce = Math.Max(MostAtOnce, now);
                if (OperatingSystem.IsWindows() && Thread.CurrentThread.Priority != ThreadPriority.BelowNormal)
                    LowPriority = false;
            }
            Go.Wait();
            // Early pieces take longest, so they finish out of order.
            Thread.Sleep(Math.Max(0, 60 - (int)offset));
            Interlocked.Decrement(ref _running);
            return [new Word($"at{offset:0}", offset, offset + 0.5)];
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Pieces_are_recognized_several_at_once_and_the_words_come_out_in_order()
    {
        var recognizer = new ParallelRecognizer(3);
        var progress = new List<double>();
        var pieces = new List<string>();

        var words = await TranscriptionPipeline.RunAsync(Audio(), 70, recognizer, (found, p) =>
        {
            pieces.AddRange(found.Select(w => w.Text));
            progress.Add(p);
        }, TestContext.Current.CancellationToken);

        Assert.InRange(recognizer.MostAtOnce, 2, 3);
        Assert.True(recognizer.LowPriority);
        Assert.Equal(["at0", "at21", "at44"], words.Select(w => w.Text));
        Assert.Equal(["at0", "at21", "at44"], pieces);
        Assert.Equal(progress.Order(), progress);
        Assert.Equal(1, progress[^1], 3);
    }

    [Fact]
    public async Task Stopping_waits_for_the_pieces_under_way()
    {
        var recognizer = new ParallelRecognizer(3);
        recognizer.Go.Reset();
        using var cts = new CancellationTokenSource();
        var audio = Audio();
        // Stopped once the audio has been read, while the pieces are held.
        var run = TranscriptionPipeline.RunAsync(new StopAtEnd(audio, cts), 70, recognizer, cancellationToken: cts.Token);
        while (recognizer.Running == 0)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);

        recognizer.Go.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        // The recognizer can be disposed now: nothing is using it.
        Assert.Equal(0, recognizer.Running);
    }

    /// <summary>Cancels when the audio runs out, before the last piece is started.</summary>
    private sealed class StopAtEnd(Stream inner, CancellationTokenSource cts) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, CancellationToken.None);
            if (read == 0)
                await cts.CancelAsync();
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void The_cut_goes_to_the_quietest_moment()
    {
        var samples = Enumerable.Range(0, 16000 * 3).Select(i => i is > 30000 and < 33000 ? 0f : 0.5f).ToArray();
        Assert.InRange(TranscriptionPipeline.QuietestCut(samples, 16000, 48000), 30000, 33000);
    }
}

public class RecognizerPlanTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(4, 4, 1)]
    [InlineData(6, 4, 1)]
    [InlineData(16, 4, 4)]
    [InlineData(32, 4, 8)]
    public void The_CPU_plan_uses_every_core(int cores, int parallelism, int threads)
    {
        var plan = RecognizerPlan.Cpu(cores);
        Assert.Equal(("cpu", parallelism, threads), (plan.Provider, plan.Parallelism, plan.Threads));
        Assert.False(plan.OnGpu);
        Assert.Equal($"CPU · {parallelism * threads} threads", plan.Description);
    }

    [Fact]
    public void The_device_setting_picks_the_provider()
    {
        Assert.Equal("cpu", RecognizerPlan.Choose(TranscriptionDevice.Auto, 8, null).Provider);
        Assert.Equal("cpu", RecognizerPlan.Choose(TranscriptionDevice.Cpu, 8, "cuda").Provider);
        Assert.Equal("cuda", RecognizerPlan.Choose(TranscriptionDevice.Auto, 8, "cuda").Provider);
        Assert.Equal("directml", RecognizerPlan.Choose(TranscriptionDevice.Gpu, 8, "directml").Provider);
        Assert.Equal("GPU (DirectML)", RecognizerPlan.Choose(TranscriptionDevice.Gpu, 8, "directml").Description);
        var e = Assert.Throws<InvalidOperationException>(() => RecognizerPlan.Choose(TranscriptionDevice.Gpu, 8, null));
        Assert.Contains("Choose Auto or CPU", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_falls_back_to_the_CPU_when_the_GPU_does_not_start()
    {
        var tried = new List<string>();
        RecognizerPlan Make(RecognizerPlan plan)
        {
            tried.Add(plan.Provider);
            return plan.OnGpu ? throw new DllNotFoundException("cudnn64_9.dll") : plan;
        }

        Assert.Equal(RecognizerPlan.Cpu(8), RecognizerPlan.Create(TranscriptionDevice.Auto, 8, "cuda", Make));
        Assert.Equal(["cuda", "cpu"], tried);

        tried.Clear();
        Assert.Throws<DllNotFoundException>(() => RecognizerPlan.Create(TranscriptionDevice.Gpu, 8, "cuda", Make));
        Assert.Equal(["cuda"], tried);

        tried.Clear();
        RecognizerPlan.Create(TranscriptionDevice.Cpu, 8, "cuda", Make);
        Assert.Equal(["cpu"], tried);
    }

    [Fact]
    public void A_GPU_runtime_is_found_beside_the_app()
    {
        string dir = Directory.CreateTempSubdirectory("highlightcut-gpu").FullName;
        try
        {
            Assert.Null(RecognizerPlan.FindGpuProvider(dir));
            string native = Directory.CreateDirectory(Path.Combine(dir, "runtimes", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "native")).FullName;
            File.WriteAllText(Path.Combine(native, OperatingSystem.IsWindows() ? "onnxruntime_providers_cuda.dll" : "libonnxruntime_providers_cuda.so"), "");
            Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ? "cuda" : null, RecognizerPlan.FindGpuProvider(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>
/// Recognizes real speech with an installed model. Runs when HIGHLIGHTCUT_MODELS_DIR has the model (see
/// RealDownloadTests); skipped otherwise.
/// </summary>
public class RealRecognitionTests
{
    private static string? ModelDirectory(TranscriptionModel model) =>
        Environment.GetEnvironmentVariable("HIGHLIGHTCUT_MODELS_DIR") is { Length: > 0 } dir && new ModelStore(dir).IsInstalled(model)
            ? new ModelStore(dir).DirectoryOf(model)
            : null;

    /// <summary>16 kHz mono float samples of a file, via ffmpeg.</summary>
    private static async Task<byte[]> Pcm(string path, double padSeconds = 0)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true };
        foreach (string a in (string[])["-v", "error", "-i", path, "-af", $"apad=pad_dur={padSeconds}", "-ac", "1", "-ar", "16000", "-f", "f32le", "pipe:1"])
            psi.ArgumentList.Add(a);
        using var pcm = new MemoryStream();
        using var ffmpeg = Process.Start(psi)!;
        await ffmpeg.StandardOutput.BaseStream.CopyToAsync(pcm, TestContext.Current.CancellationToken);
        await ffmpeg.WaitForExitAsync(TestContext.Current.CancellationToken);
        return pcm.ToArray();
    }

    [Fact]
    public async Task Whisper_transcribes_speech_with_word_times()
    {
        var model = ModelCatalog.Find("whisper-base.en")!;
        string? dir = ModelDirectory(model);
        Assert.SkipWhen(dir is null, "whisper-base.en is not installed in HIGHLIGHTCUT_MODELS_DIR.");
        byte[] pcm = await Pcm(Path.Combine(dir!, "test_wavs", "0.wav"));
        using var audio = new MemoryStream(pcm);

        using var recognizer = new SherpaRecognizer(model, dir!, ModelCatalog.OnlyLanguage(model));
        var words = await TranscriptionPipeline.RunAsync(audio, pcm.Length / 4.0 / 16000, recognizer, cancellationToken: TestContext.Current.CancellationToken);

        string text = string.Join(' ', words.Select(w => w.Text));
        Assert.Contains("yellow lamps would light up", text, StringComparison.OrdinalIgnoreCase);
        // These models give no word times: they are estimated from the audio, in order and within the speech.
        Assert.True(recognizer.HasApproximateTimes);
        Assert.True(words.Zip(words.Skip(1)).All(p => p.Second.Start >= p.First.End - 1e-9), text);
        Assert.InRange(words[0].Start, 0, 1);
        Assert.InRange(words[^1].End, 5, 7);
    }

    [Fact]
    public async Task Parakeet_transcribes_speech_with_word_times()
    {
        string? dir = ModelDirectory(ModelCatalog.Parakeet);
        Assert.SkipWhen(dir is null, "Parakeet is not installed in HIGHLIGHTCUT_MODELS_DIR.");
        // The model's own sample, three times with pauses: pieces and offsets are exercised too.
        byte[] pcm = await Pcm(Path.Combine(dir!, "test_wavs", "en.wav"), 12);
        using var audio = new MemoryStream([.. pcm, .. pcm, .. pcm]);
        double duration = pcm.Length / 4.0 / 16000 * 3;

        using var recognizer = new SherpaRecognizer(ModelCatalog.Parakeet, dir!);
        var words = await TranscriptionPipeline.RunAsync(audio, duration, recognizer, cancellationToken: TestContext.Current.CancellationToken);

        string text = string.Join(' ', words.Select(w => w.Text)).ToLowerInvariant();
        string timed = string.Join(" ", words.Select(w => $"{w.Text}@{w.Start:0.00}-{w.End:0.00}"));
        Assert.True(text.Split("ask not what your country can do for you").Length - 1 == 3, timed);
        var ask = words.Where((w, i) => w.Text.Equals("Ask", StringComparison.OrdinalIgnoreCase) && i + 1 < words.Count && words[i + 1].Text == "not")
            .Select(w => w.Start).ToList();
        Assert.Equal(3, ask.Count);
        Assert.Equal(ask[0] + duration / 3, ask[1], 0.3);
        Assert.Equal(ask[0] + 2 * duration / 3, ask[2], 0.3);
        Assert.All(words, w => Assert.True(w.End >= w.Start));
    }
}
