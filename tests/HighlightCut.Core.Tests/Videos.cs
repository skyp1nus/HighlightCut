using HighlightCut.Core.Editing;
using HighlightCut.Core.Model;

namespace HighlightCut.Core.Tests;

/// <summary>
/// Projects with several videos end to end: a.mp4 (100 s, id 1), b.mp4 (50 s, id 2) and c.mp4 (30 s, id 3), so the
/// timeline runs a 0–100, b 100–150, c 150–180.
/// </summary>
internal static class Videos
{
    public static SourceMedia A { get; } = new("/v/a.mp4", 100, 30, [new AudioTrack(1, "Mic")], 1);
    public static SourceMedia B { get; } = new("/v/b.mp4", 50, 25, [new AudioTrack(1, "Mic"), new AudioTrack(2, "Game")], 2);
    public static SourceMedia C { get; } = new("/v/c.mp4", 30, 60, [], 3);

    /// <summary>Two videos, a and b, with a clip in each at the same seconds of its own video.</summary>
    public static Project Two { get; } = new Project("two", [A, B],
    [
        new Clip(1, "A intro", 10, 20, Color: ClipColor.Teal, SourceId: 1),
        new Clip(2, "B intro", 10, 20, Color: ClipColor.Amber, SourceId: 2),
    ]).WithSources([A, B]);

    /// <summary>All three videos, without clips.</summary>
    public static Project Three { get; } = new Project("three", [A, B, C], []).WithSources([A, B, C]);

    public static EditorSession Open(Project project) => new(project);
}
