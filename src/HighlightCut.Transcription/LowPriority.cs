using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HighlightCut.Transcription;

/// <summary>
/// Recognition runs below normal priority, like the media analysis: it gets the CPU that playback and the UI leave.
/// </summary>
internal static partial class LowPriority
{
    /// <summary>The nice value used on Linux (0 is normal, 19 the lowest).</summary>
    private const int Nice = 10;

    /// <summary>Lowers the calling thread; for threads that do nothing else.</summary>
    public static void LowerCurrentThread()
    {
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        }
        catch (Exception e) when (e is ThreadStateException or PlatformNotSupportedException)
        {
        }
        // .NET does not change thread priorities on Linux; there each thread has its own nice value, and
        // who = 0 means the calling thread.
        if (OperatingSystem.IsLinux())
            _ = setpriority(0, 0, Nice);
    }

    /// <summary>
    /// Runs <paramref name="start"/> and lowers the threads it starts (ONNX Runtime's thread pools, made with the
    /// recognizer). Threads the process starts meanwhile for something else are lowered too, so this is only used
    /// when the recognizer does start threads.
    /// </summary>
    public static T LowerThreadsStartedBy<T>(Func<T> start)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return start();
        var before = ThreadIds();
        var result = start();
        foreach (ProcessThread thread in Process.GetCurrentProcess().Threads)
        {
            using (thread)
            {
                if (before.Contains(thread.Id))
                    continue;
                try
                {
                    if (OperatingSystem.IsWindows())
                        thread.PriorityLevel = ThreadPriorityLevel.BelowNormal;
                    else
                        _ = setpriority(0, thread.Id, Nice);
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Already exited.
                }
            }
        }
        return result;
    }

    private static HashSet<int> ThreadIds()
    {
        var ids = new HashSet<int>();
        foreach (ProcessThread thread in Process.GetCurrentProcess().Threads)
        {
            using (thread)
                ids.Add(thread.Id);
        }
        return ids;
    }

    /// <param name="which">PRIO_PROCESS (0): on Linux, <paramref name="who"/> is a thread id.</param>
    [LibraryImport("libc")]
    private static partial int setpriority(int which, int who, int prio);
}
