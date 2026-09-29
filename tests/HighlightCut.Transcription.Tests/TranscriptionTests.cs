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
    public void Auto_uses_DirectML_only_once_the_check_finds_it_works_and_is_faster()
    {
        var faster = new GpuCheck(true, 3.2);
        // Unchecked, failed or slower: the CPU.
        Assert.Equal(RecognizerPlan.Cpu(8), RecognizerPlan.Choose(TranscriptionDevice.Auto, 8, "directml"));
        Assert.Equal(RecognizerPlan.Cpu(8), RecognizerPlan.Choose(TranscriptionDevice.Auto, 8, "directml", GpuCheck.NoGpu));
        Assert.Equal(RecognizerPlan.Cpu(8), RecognizerPlan.Choose(TranscriptionDevice.Auto, 8, "directml", new GpuCheck(true, 0.7)));
        // One piece at a time on the GPU: DirectML sessions run one call at a time.
        Assert.Equal(new RecognizerPlan("directml", 1, 4), RecognizerPlan.Choose(TranscriptionDevice.Auto, 8, "directml", faster));
        Assert.Equal(new RecognizerPlan("directml", 1, 2), RecognizerPlan.DirectML(2));

        // GPU asked for: DirectML even when slower or unchecked; a failed check is reported instead of risking it.
        Assert.Equal("directml", RecognizerPlan.Choose(TranscriptionDevice.Gpu, 8, "directml", new GpuCheck(true, 0.7)).Provider);
        var e = Assert.Throws<InvalidOperationException>(() => RecognizerPlan.Choose(TranscriptionDevice.Gpu, 8, "directml", GpuCheck.NoGpu));
        Assert.Equal("DirectML does not work here: there is no GPU it can use. Choose Auto or CPU in Settings → Transcription.", e.Message);
        Assert.Equal("cpu", RecognizerPlan.Choose(TranscriptionDevice.Cpu, 8, "directml", faster).Provider);
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

public sealed class GpuProbeTests : IDisposable
{
    private static readonly GpuAdapter Radeon = new("AMD Radeon RX 7800 XT", 0x1002, 0x747e, "32.0.21013.1000", IsSoftware: false);
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-gpu-probe").FullName;

    public GpuProbeTests()
    {
        GpuProbe.Adapter = Radeon;
        GpuProbe.CacheFile = Path.Combine(_dir, "gpu-check.json");
        GpuProbe.Reset();
    }

    public void Dispose()
    {
        GpuProbe.Adapter = GpuAdapter.Current;
        GpuProbe.Command = null;
        GpuProbe.CacheFile = null;
        GpuProbe.Reset();
        Directory.Delete(_dir, recursive: true);
    }

    private const string Result = """{"gpuSeconds":0.5,"cpuSeconds":4,"cpuPieces":4,"gpuWords":9,"cpuWords":9,"sameWords":true}""";

    /// <summary>A child that prints <paramref name="output"/> (and <paramref name="errors"/> on stderr) and counts its runs.</summary>
    private void FakeChild(string output, string errors = "", int exitCode = 0)
    {
        string runs = Path.Combine(_dir, "runs.txt");
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(_dir, "child.cmd");
            File.WriteAllText(script, $"@echo run>>\"{runs}\"\r\n@echo {output}\r\n" + (errors.Length > 0 ? $"@echo {errors} 1>&2\r\n" : "") + $"@exit /b {exitCode}\r\n");
            GpuProbe.Command = ("cmd.exe", ["/c", script]);
        }
        else
        {
            string script = Path.Combine(_dir, "child.sh");
            File.WriteAllText(script, $"echo run >> '{runs}'\necho '{output}'\n" + (errors.Length > 0 ? $"echo '{errors}' >&2\n" : "") + $"exit {exitCode}\n");
            GpuProbe.Command = ("/bin/sh", [script]);
        }
    }

    private int Runs => File.Exists(Path.Combine(_dir, "runs.txt")) ? File.ReadAllLines(Path.Combine(_dir, "runs.txt")).Length : 0;

    [Fact]
    public void The_check_runs_once_per_GPU_and_model_and_is_remembered_between_runs()
    {
        FakeChild(Result);
        Assert.Null(GpuProbe.Known(ModelCatalog.Parakeet));

        var check = GpuProbe.Check(ModelCatalog.Parakeet, _dir, null);

        // 4 pieces in 4 s on the CPU against one in 0.5 s on the GPU: twice as fast.
        Assert.Equal(new GpuCheck(true, 2.0), check);
        Assert.Equal("2.0×", check!.SpeedupText);
        Assert.Equal(check, GpuProbe.Check(ModelCatalog.Parakeet, _dir, null));
        Assert.Equal(1, Runs);
        GpuProbe.Reset();
        Assert.Equal(check, GpuProbe.Known(ModelCatalog.Parakeet));
        // Another model, or a new driver, is checked again.
        Assert.Null(GpuProbe.Known(ModelCatalog.Find("whisper-base.en")));
        GpuProbe.Adapter = Radeon with { DriverVersion = "32.0.21025.1000" };
        Assert.Null(GpuProbe.Known(ModelCatalog.Parakeet));
    }

    [Fact]
    public void Without_a_GPU_nothing_is_run()
    {
        FakeChild(Result);
        GpuProbe.Adapter = new GpuAdapter("Microsoft Basic Render Driver", 0x1414, 0x8c, "10.0.26100.1", IsSoftware: true);
        Assert.Equal(GpuCheck.NoGpu, GpuProbe.Check(ModelCatalog.Parakeet, _dir, null));
        GpuProbe.Adapter = null;
        Assert.Equal(GpuCheck.NoGpu, GpuProbe.Known(null));
        Assert.Equal(0, Runs);
    }

    [Fact]
    public void Without_a_command_the_GPU_is_not_checked()
    {
        GpuProbe.Command = null;
        Assert.Null(GpuProbe.Check(ModelCatalog.Parakeet, _dir, null));
        // A child that cannot start is not the GPU's fault: tried again next time.
        GpuProbe.Command = (Path.Combine(_dir, "missing.exe"), []);
        Assert.True(GpuProbe.Check(ModelCatalog.Parakeet, _dir, null)!.Retry);
        Assert.Null(GpuProbe.Known(ModelCatalog.Parakeet));
    }

    [Fact]
    public void A_child_that_crashes_counts_as_DirectML_not_working()
    {
        FakeChild("", "access violation", exitCode: 3);
        var check = GpuProbe.Check(ModelCatalog.Parakeet, _dir, null)!;
        Assert.False(check.Works);
        Assert.Equal("the check stopped (exit code 3): access violation", check.Problem);
    }

    [Fact]
    public void DirectML_that_falls_back_to_the_CPU_does_not_work()
    {
        // What sherpa-onnx prints when ONNX Runtime refuses the adapter, e.g. the basic display driver.
        string errors = @"D:\a\sherpa-onnx\sherpa-onnx\csrc\session.cc:GetSessionOptionsImpl:369 Failed to enable DirectML: "
            + @"D:\a\_work\1\s\onnxruntime\core\providers\dml\dml_provider_factory.cc(519)\onnxruntime.dll!00007FFE2B1C3F2A: (caller: 00007FFE2B1C2E11) "
            + "Exception(1) tid(1a2c) 887A0004 The specified device interface or feature level is not supported on this system.\r\n. Fallback to cpu";
        var check = GpuProbe.Interpret(0, Result, errors);
        Assert.Equal(new GpuCheck(false, Problem: "it did not start (The specified device interface or feature level is not supported on this system)"), check);
        Assert.False(check.Faster);
        Assert.Equal("it did not start (DirectML is for Windows only)",
            GpuProbe.Interpret(0, Result, "/workspace/sherpa-onnx/csrc/session.cc:GetSessionOptionsImpl:373 DirectML is for Windows only. Fallback to cpu!\n").Problem);
    }

    [Fact]
    public void DirectML_that_hears_nothing_does_not_work_and_different_words_are_noted()
    {
        Assert.Equal("it recognized no words", GpuProbe.Interpret(0, Result.Replace("\"gpuWords\":9", "\"gpuWords\":0", StringComparison.Ordinal), "").Problem);
        var check = GpuProbe.Interpret(0, "log line\n" + Result.Replace("true}", "false}", StringComparison.Ordinal), "");
        Assert.True(check.Works);
        Assert.False(check.SameWords);
    }

    [Fact]
    public void The_sample_is_the_model_s_recordings_one_after_another()
    {
        string wavs = Directory.CreateDirectory(Path.Combine(_dir, "test_wavs")).FullName;
        // 1 s of 16-bit WAV at half scale.
        void Wav(string name, int rate, short channels)
        {
            short[] pcm = [.. Enumerable.Repeat((short)16384, rate * channels)];
            using var w = new BinaryWriter(File.Create(Path.Combine(wavs, name)));
            w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write(channels); w.Write(rate); w.Write(rate * channels * 2);
            w.Write((short)(channels * 2)); w.Write((short)16);
            w.Write("data"u8); w.Write(pcm.Length * 2);
            foreach (short v in pcm)
                w.Write(v);
        }
        Wav("en.wav", 16000, 1);
        File.WriteAllText(Path.Combine(wavs, "notes.wav"), "not a wav");

        // Other rates and stereo (the Parakeet samples are 22.05 and 24 kHz) become 16 kHz mono.
        Wav("stereo.wav", 22050, 2);
        var stereo = GpuProbe.ReadWav(Path.Combine(wavs, "stereo.wav"))!;
        Assert.Equal(16000, stereo.Length);
        Assert.All(stereo, v => Assert.Equal(0.5f, v));
        File.Delete(Path.Combine(wavs, "stereo.wav"));
        Assert.Equal(16000, GpuProbe.ReadWav(Path.Combine(wavs, "en.wav"))!.Length);
        Assert.Null(GpuProbe.ReadWav(Path.Combine(wavs, "notes.wav")));
        var sample = GpuProbe.Sample(_dir);
        Assert.Equal(20 * 16000, sample.Length);
        // The clip, a third of a second of pause, the clip again.
        Assert.Equal(0.5f, sample[100]);
        Assert.Equal(0f, sample[16000 + 100]);
        Assert.Equal(0.5f, sample[16000 + 16000 / 3 + 100]);
        // No recordings: a tone.
        Assert.Equal(20 * 16000, GpuProbe.Sample(Path.Combine(_dir, "none")).Length);
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
    public void The_GPU_check_s_sample_is_speech_the_model_recognizes()
    {
        string? dir = ModelDirectory(ModelCatalog.Parakeet);
        Assert.SkipWhen(dir is null, "Parakeet is not installed in HIGHLIGHTCUT_MODELS_DIR.");
        using var cpu = new SherpaRecognizer(ModelCatalog.Parakeet, dir!);
        string text = string.Join(' ', cpu.Recognize(GpuProbe.Sample(dir!), 0).Select(w => w.Text));
        Assert.Contains("country", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parakeet_on_DirectML_gives_the_CPU_s_words()
    {
        string? dir = ModelDirectory(ModelCatalog.Parakeet);
        Assert.SkipWhen(dir is null, "Parakeet is not installed in HIGHLIGHTCUT_MODELS_DIR.");
        Assert.SkipUnless(RecognizerPlan.InstalledGpuProvider == "directml", "The DirectML runtime is not installed (scripts/fetch-deps.ps1).");
        float[] sample = GpuProbe.Sample(dir!);
        int cores = Environment.ProcessorCount;

        using var cpu = new SherpaRecognizer(ModelCatalog.Parakeet, dir!, plan: RecognizerPlan.Cpu(cores));
        using var gpu = new SherpaRecognizer(ModelCatalog.Parakeet, dir!, plan: RecognizerPlan.DirectML(cores));
        var cpuWords = cpu.Recognize(sample, 0).Select(w => w.Text).ToList();
        var gpuWords = gpu.Recognize(sample, 0).Select(w => w.Text).ToList();

        // Without a GPU DirectML can use (CI's basic display driver), sherpa-onnx runs the DirectML plan on the CPU.
        Assert.NotEmpty(cpuWords);
        Assert.Equal(cpuWords, gpuWords);
        if (GpuAdapter.Current is not { IsSoftware: false })
        {
            // And Auto does not try it; GPU says why it cannot.
            using var auto = SherpaRecognizer.Create(ModelCatalog.Parakeet, dir!, null, TranscriptionDevice.Auto);
            Assert.Equal(RecognizerPlan.Cpu(cores), auto.Plan);
            var e = Assert.Throws<InvalidOperationException>(() => SherpaRecognizer.Create(ModelCatalog.Parakeet, dir!, null, TranscriptionDevice.Gpu));
            Assert.Contains("there is no GPU it can use", e.Message, StringComparison.Ordinal);
        }
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
