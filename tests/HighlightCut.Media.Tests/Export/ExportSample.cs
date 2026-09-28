using System.Collections.Immutable;
using HighlightCut.Core.Model;
using HighlightCut.Media.Export;
using HighlightCut.Media.Probing;

namespace HighlightCut.Media.Tests.Export;

/// <summary>A 10 s 1080p MP4 with two audio streams, a subtitle stream and a keyframe every second.</summary>
internal static class ExportSample
{
    public static string InputFolder { get; } = Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "in"));
    public static string OutputFolder { get; } = Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "out"));
    public static string SourcePath { get; } = Path.Combine(InputFolder, "My clip's.mp4");

    public static MediaInfo Info { get; } = new(
        SourcePath, "mov,mp4,m4a,3gp,3g2,mj2", 10, 0, 8_000_000, 10_000_000,
        new VideoStreamInfo(0, "h264", 1920, 1080, 30, "30", HasBFrames: true, "yuv420p", 0),
        [
            new AudioStreamInfo(1, 0, "aac", 2, 48000, "Mic", null),
            new AudioStreamInfo(2, 1, "aac", 2, 48000, "Music", null),
        ],
        [new SubtitleStreamInfo(3, "mov_text", "eng")]);

    public static double[] Keyframes { get; } = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

    public static Project SampleProject { get; } = new("demo", Info.ToSourceMedia(),
    [
        new Clip(1, "Intro", 1.5, 3.2),
        new Clip(2, "Demo — import", 5, 7),
        new Clip(3, "Q&A", 8, 9, IsIncluded: false),
    ]);

    public static ExportSettings Settings(CutMode mode = CutMode.Lossless, bool merge = true) => new()
    {
        Mode = mode,
        Merge = merge,
        OutputFolder = OutputFolder,
        BaseName = "demo",
    };

    public static ExportPlan Plan(ExportSettings settings, Project? project = null, MediaInfo? info = null,
        Func<string, bool>? fileExists = null) =>
        ExportPlanner.Plan(project ?? SampleProject, info ?? Info, Keyframes, settings, fileExists ?? (_ => false), tempId: "t1");

    public static string Out(string name) => Path.Combine(OutputFolder, name);
}
