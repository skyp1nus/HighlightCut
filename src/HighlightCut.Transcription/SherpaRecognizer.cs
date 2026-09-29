using HighlightCut.Core.Transcripts;
using HighlightCut.Transcription.Models;
using SherpaOnnx;

namespace HighlightCut.Transcription;

/// <summary>
/// Speech recognition with sherpa-onnx (ONNX Runtime; the CPU unless a GPU build of it is installed): Parakeet TDT
/// through its transducer, Whisper through its encoder/decoder with token timestamps. Both give subword tokens with
/// start times, turned into words by <see cref="WordBuilder"/>. Several pieces can be recognized at once, from
/// different threads, with the one model in memory.
/// </summary>
public sealed class SherpaRecognizer : ISpeechRecognizer
{
    private readonly OfflineRecognizer _recognizer;

    /// <param name="directory">The installed model's folder.</param>
    /// <param name="language">Two-letter code for Whisper ("en", "uk"), or null to detect it. Parakeet always detects.</param>
    /// <param name="plan">Provider, pieces at once and threads for each; all the cores of the CPU by default.</param>
    public SherpaRecognizer(TranscriptionModel model, string directory, string? language = null, RecognizerPlan? plan = null)
    {
        Plan = plan ?? RecognizerPlan.Cpu(Environment.ProcessorCount);
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = TranscriptionPipeline.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.DecodingMethod = "greedy_search";
        config.ModelConfig.NumThreads = Plan.Threads;
        config.ModelConfig.Provider = Plan.Provider;
        config.ModelConfig.Tokens = Path.Combine(directory, model.Files.Single(f => f.EndsWith("tokens.txt", StringComparison.Ordinal)));
        string File(string part) => Path.Combine(directory, model.Files.Single(f => f.Contains(part, StringComparison.Ordinal)));
        switch (model.Engine)
        {
            case TranscriptionEngine.Parakeet:
                config.ModelConfig.ModelType = "nemo_transducer";
                config.ModelConfig.Transducer.Encoder = File("encoder");
                config.ModelConfig.Transducer.Decoder = File("decoder");
                config.ModelConfig.Transducer.Joiner = File("joiner");
                break;
            case TranscriptionEngine.Whisper:
                config.ModelConfig.Whisper.Encoder = File("encoder");
                config.ModelConfig.Whisper.Decoder = File("decoder");
                config.ModelConfig.Whisper.Language = language ?? "";
                config.ModelConfig.Whisper.Task = "transcribe";
                config.ModelConfig.Whisper.TailPaddings = -1;
                // Only models exported with attention outputs have token times; the others get estimated ones.
                config.ModelConfig.Whisper.EnableTokenTimestamps = 0;
                break;
        }
        if (Plan.Provider == "directml")
            RecognizerPlan.LoadDirectML();
        // With one thread a piece runs on the pipeline's low-priority thread; with more, ONNX Runtime starts its own.
        _recognizer = Plan.Threads > 1 ? LowPriority.LowerThreadsStartedBy(() => new OfflineRecognizer(config)) : new OfflineRecognizer(config);
    }

    /// <summary>
    /// Makes the recognizer for Settings → Transcription → Device: Auto uses the GPU when its runtime is installed,
    /// the DirectML check (<see cref="GpuProbe"/>, run the first time) finds it works and is faster, and it starts;
    /// otherwise the CPU. GPU reports why it cannot.
    /// </summary>
    public static SherpaRecognizer Create(TranscriptionModel model, string directory, string? language, TranscriptionDevice device)
    {
        string? gpu = RecognizerPlan.InstalledGpuProvider;
        var check = gpu == "directml" && device != TranscriptionDevice.Cpu ? GpuProbe.Check(model, directory, language) : null;
        return RecognizerPlan.Create(device, Environment.ProcessorCount, gpu, plan => new SherpaRecognizer(model, directory, language, plan), check);
    }

    public RecognizerPlan Plan { get; }

    /// <summary>
    /// Decoding keeps its state in the stream; the model's ONNX Runtime sessions may run from several threads at once
    /// on the CPU (DirectML plans take one piece at a time). (Whisper rewrites its decoder settings on every call,
    /// always with the same values.)
    /// </summary>
    public int Parallelism => Plan.Parallelism;

    public IReadOnlyList<Word> Recognize(float[] samples, double offset)
    {
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(TranscriptionPipeline.SampleRate, samples);
        _recognizer.Decode(stream);
        var result = stream.Result;
        if (result.Tokens is not { Length: > 0 } tokens)
            return [];
        if (result.Timestamps is { } times && times.Length == tokens.Length)
            return WordBuilder.Build(tokens, times, offset, offset + (double)samples.Length / TranscriptionPipeline.SampleRate);
        HasApproximateTimes = true;
        return WordBuilder.Estimate(WordBuilder.Texts(tokens), samples, offset);
    }

    /// <summary>Some words had no times from the model and were given estimated ones.</summary>
    public bool HasApproximateTimes { get; private set; }

    public void Dispose() => _recognizer.Dispose();
}
