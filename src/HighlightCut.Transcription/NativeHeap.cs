using System.Runtime.InteropServices;

namespace HighlightCut.Transcription;

/// <summary>Hands memory that native code has freed back to the system.</summary>
internal static partial class NativeHeap
{
    /// <summary>
    /// glibc keeps what ONNX Runtime frees (the model and its buffers, most of a gigabyte) in malloc's arenas for reuse,
    /// so on Linux the process would stay as large after a transcription as during it. Windows returns such large
    /// blocks by itself. Returns whether malloc was asked to (glibc on Linux).
    /// </summary>
    public static bool Trim()
    {
        if (!OperatingSystem.IsLinux())
            return false;
        try
        {
            _ = MallocTrim(0);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not glibc (musl has no malloc_trim).
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "malloc_trim")]
    private static partial int MallocTrim(nuint pad);
}
