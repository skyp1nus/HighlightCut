using HighlightCut.Media.Tools;

namespace HighlightCut.Media.Tests.Integration;

/// <summary>
/// Generates small test files with ffmpeg once per test class: a 10 s 320×180 H.264 MP4 at 30 fps
/// with a keyframe every second, B-frames and two named AAC streams, plus an MKV remux of it. MP4 keeps
/// the track names in the handler name, MKV in the title. In the MKV the video starts 23 ms late.
/// Tests skip themselves when ffmpeg is not installed.
/// </summary>
public sealed class SampleMediaFixture : IAsyncLifetime
{
    public string Folder { get; } = Directory.CreateTempSubdirectory("highlightcut-it").FullName;
    public string Mp4 => Path.Combine(Folder, "sample clip.mp4");
    public string Mkv => Path.Combine(Folder, "sample clip.mkv");

    public bool IsAvailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (NativeTools.FindTool("ffmpeg") is null || NativeTools.FindTool("ffprobe") is null)
            return;
        await ToolProcess.RunAsync("ffmpeg",
        [
            "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-f", "lavfi", "-i", "sine=frequency=440",
            "-f", "lavfi", "-i", "sine=frequency=880", "-t", "10", "-map", "0:v", "-map", "1:a", "-map", "2:a",
            "-c:v", "libx264", "-preset", "veryfast", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0", "-bf", "2",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-metadata:s:a:0", "handler_name=Mic", "-metadata:s:a:1", "handler_name=Music",
            "-y", Mp4,
        ], null, CancellationToken.None);
        await ToolProcess.RunAsync("ffmpeg",
        [
            "-v", "error", "-i", Mp4, "-map", "0", "-c", "copy", "-metadata:s:a:0", "title=Mic", "-metadata:s:a:1", "title=Music",
            "-y", Mkv,
        ], null, CancellationToken.None);
        IsAvailable = true;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Temp folder; the OS cleans it up.
        }
        return ValueTask.CompletedTask;
    }

    public void SkipIfUnavailable() => Assert.SkipUnless(IsAvailable, "ffmpeg/ffprobe are not installed.");

    /// <summary>A fresh empty folder for one test's output.</summary>
    public string NewOutputFolder() => Directory.CreateDirectory(Path.Combine(Folder, "out-" + Guid.NewGuid().ToString("N")[..8])).FullName;
}
