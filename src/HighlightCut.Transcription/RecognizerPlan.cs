using System.Runtime.InteropServices;

namespace HighlightCut.Transcription;

/// <summary>Where Settings → Transcription → Device asks speech to be recognized.</summary>
public enum TranscriptionDevice
{
    /// <summary>The GPU when its runtime is installed, works and is faster, otherwise the CPU.</summary>
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
    /// One piece at a time: a DirectML session runs one call at a time (ONNX Runtime's DirectML provider needs
    /// sequential execution), and a second copy of the model on the GPU would only compete for it. A few threads run
    /// what stays on the CPU.
    /// </summary>
    public static RecognizerPlan DirectML(int cores) => new("directml", 1, Math.Clamp(cores, 1, 4));

    /// <summary>
    /// The plan for <paramref name="device"/>. <paramref name="gpuProvider"/> is the GPU provider whose runtime is
    /// installed (<see cref="InstalledGpuProvider"/>), or null; <paramref name="check"/> is what the DirectML check
    /// found, or null if it could not run.
    /// </summary>
    /// <exception cref="InvalidOperationException">The GPU was asked for and has no runtime, or DirectML does not work.</exception>
    public static RecognizerPlan Choose(TranscriptionDevice device, int cores, string? gpuProvider, GpuCheck? check = null)
    {
        if (device == TranscriptionDevice.Cpu || (gpuProvider is null && device == TranscriptionDevice.Auto))
            return Cpu(cores);
        if (gpuProvider is null)
            throw new InvalidOperationException(NoGpuMessage);
        if (gpuProvider != "directml")
            return new(gpuProvider, 2, 2);
        // Without a check, Auto does not risk DirectML: a failing driver can end the process.
        if (device == TranscriptionDevice.Auto)
            return check is { Faster: true } ? DirectML(cores) : Cpu(cores);
        return check is { Works: false }
            ? throw new InvalidOperationException($"DirectML does not work here: {check.Problem}. Choose Auto or CPU in Settings → Transcription.")
            : DirectML(cores);
    }

    public const string NoGpuMessage =
        "Transcription on the GPU needs a GPU runtime, and this build of HighlightCut has none. Choose Auto or CPU in Settings → Transcription.";

    /// <summary>
    /// Makes the recognizer for <paramref name="device"/>. With Auto, a GPU that fails to start falls back to the
    /// CPU; with GPU, the failure is reported.
    /// </summary>
    public static T Create<T>(TranscriptionDevice device, int cores, string? gpuProvider, Func<RecognizerPlan, T> create, GpuCheck? check = null)
    {
        var plan = Choose(device, cores, gpuProvider, check);
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
    /// "directml" for the DirectML one), or null for the CPU-only runtime.
    /// </summary>
    public static string? InstalledGpuProvider { get; } = FindGpuProvider(AppContext.BaseDirectory);

    /// <summary>Folders the native runtime is loaded from: beside the app, or in runtimes/&lt;rid&gt;/native (builds without a RID).</summary>
    private static string[] NativeFolders(string appDirectory) =>
        [appDirectory, Path.Combine(appDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native")];

    internal static string? FindGpuProvider(string appDirectory)
    {
        var folders = NativeFolders(appDirectory);
        bool Has(string file) => folders.Any(f => File.Exists(Path.Combine(f, file)));
        if (OperatingSystem.IsWindows())
            return Has("onnxruntime_providers_cuda.dll") ? "cuda" : Has("DirectML.dll") ? "directml" : null;
        return OperatingSystem.IsLinux() && Has("libonnxruntime_providers_cuda.so") ? "cuda" : null;
    }

    /// <summary>
    /// Loads the DirectML library beside the runtime before ONNX Runtime asks for it by name, so Windows' own older
    /// copy in System32 is not the one it gets (builds without a RID keep the runtime out of the app's folder).
    /// </summary>
    internal static void LoadDirectML()
    {
        if (NativeFolders(AppContext.BaseDirectory).Select(f => Path.Combine(f, "DirectML.dll")).FirstOrDefault(File.Exists) is { } path)
            NativeLibrary.Load(path);
    }

    /// <summary>The sizes of the installed runtime's libraries: a new runtime gets checked again.</summary>
    internal static string GpuRuntimeStamp { get; } = string.Join(",", ((string[])["sherpa-onnx-c-api.dll", "onnxruntime.dll", "DirectML.dll"])
        .Select(file => NativeFolders(AppContext.BaseDirectory).Select(f => new FileInfo(Path.Combine(f, file))).FirstOrDefault(i => i.Exists)?.Length ?? 0));
}
