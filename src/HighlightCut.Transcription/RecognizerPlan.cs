using System.Runtime.InteropServices;

namespace HighlightCut.Transcription;

/// <summary>Where Settings → Transcription → Device asks speech to be recognized.</summary>
public enum TranscriptionDevice
{
    /// <summary>The GPU when its runtime is installed and starts, otherwise the CPU.</summary>
    Auto,
    Gpu,
    Cpu,
}

/// <summary>
/// How a recognizer runs: the ONNX Runtime provider ("cpu", "cuda", "directml"), how many pieces it recognizes at
/// once and how many threads each piece gets.
/// </summary>
public sealed record RecognizerPlan(string Provider, int Parallelism, int Threads)
{
    /// <summary>Most pieces recognized at once: each holds its own buffers (100–200 MB), and more gain little.</summary>
    public const int MaxParallelism = 4;

    public bool OnGpu => Provider != "cpu";

    /// <summary>All the cores: up to <see cref="MaxParallelism"/> pieces at once, the cores shared among them.</summary>
    public static RecognizerPlan Cpu(int cores)
    {
        cores = Math.Max(1, cores);
        int parallelism = Math.Min(cores, MaxParallelism);
        return new("cpu", parallelism, Math.Max(1, cores / parallelism));
    }

    /// <summary>
    /// The plan for <paramref name="device"/>. <paramref name="gpuProvider"/> is the GPU provider whose runtime is
    /// installed (<see cref="InstalledGpuProvider"/>), or null.
    /// </summary>
    /// <exception cref="InvalidOperationException">The GPU was asked for and has no runtime.</exception>
    public static RecognizerPlan Choose(TranscriptionDevice device, int cores, string? gpuProvider) => device switch
    {
        TranscriptionDevice.Cpu => Cpu(cores),
        _ when gpuProvider is not null => new(gpuProvider, 2, 2),
        TranscriptionDevice.Gpu => throw new InvalidOperationException(NoGpuMessage),
        _ => Cpu(cores),
    };

    public const string NoGpuMessage =
        "Transcription on the GPU needs a GPU runtime, and this build of HighlightCut has none. Choose Auto or CPU in Settings → Transcription.";

    /// <summary>
    /// Makes the recognizer for <paramref name="device"/>. With Auto, a GPU that fails to start falls back to the
    /// CPU; with GPU, the failure is reported.
    /// </summary>
    public static T Create<T>(TranscriptionDevice device, int cores, string? gpuProvider, Func<RecognizerPlan, T> create)
    {
        var plan = Choose(device, cores, gpuProvider);
        if (!plan.OnGpu || device == TranscriptionDevice.Gpu)
            return create(plan);
        try
        {
            return create(plan);
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException
                                      or BadImageFormatException or ExternalException)
        {
            return create(Cpu(cores));
        }
    }

    /// <summary>"CPU · 16 threads", "GPU (CUDA)".</summary>
    public string Description => OnGpu ? $"GPU ({(Provider == "directml" ? "DirectML" : Provider.ToUpperInvariant())})"
        : $"CPU · {Parallelism * Threads} threads";

    /// <summary>
    /// The GPU provider whose ONNX Runtime library sits beside the app ("cuda" for the CUDA build of sherpa-onnx,
    /// "directml" for the DirectML one), or null for the CPU-only runtime HighlightCut ships with.
    /// </summary>
    public static string? InstalledGpuProvider { get; } = FindGpuProvider(AppContext.BaseDirectory);

    internal static string? FindGpuProvider(string appDirectory)
    {
        string[] folders = [appDirectory, Path.Combine(appDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native")];
        bool Has(string file) => folders.Any(f => File.Exists(Path.Combine(f, file)));
        if (OperatingSystem.IsWindows())
            return Has("onnxruntime_providers_cuda.dll") ? "cuda" : Has("DirectML.dll") ? "directml" : null;
        return OperatingSystem.IsLinux() && Has("libonnxruntime_providers_cuda.so") ? "cuda" : null;
    }
}
