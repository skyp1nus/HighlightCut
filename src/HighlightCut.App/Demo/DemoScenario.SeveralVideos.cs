using HighlightCut.App.ViewModels;
using HighlightCut.Core.Model;

namespace HighlightCut.App.Demo;

public static partial class DemoScenario
{
    /// <summary>
    /// Three videos end to end (the design's sample three times: a keynote in three parts, the second with a second audio
    /// track): clips in each, two of them meeting at the join between parts 1 and 2, the playhead in part 2.
    /// </summary>
    static partial void ApplySeveralVideos(EditorViewModel editor, DesignScreen screen)
    {
        if (screen != DesignScreen.SeveralVideos)
            return;
        var first = DesignSample.Source with { Id = 1, Path = "keynote_part1.mp4" };
        var second = DesignSample.Source with { Id = 2, Path = "keynote_part2.mp4", AudioTracks = [new AudioTrack(1, "Stage"), new AudioTrack(2, "Audience")] };
        var third = DesignSample.Source with { Id = 3, Path = "keynote_part3.mp4" };
        var project = new Project("keynote", [first, second, third],
        [
            new Clip(1, "Cold open", 12, 45.2, Color: ClipColor.Teal),
            new Clip(2, "Setup walkthrough", 262.08, 365.52, Color: ClipColor.Amber),
            new Clip(3, "End of part 1", 790, DesignSample.SampleDuration, Color: ClipColor.Violet) { SourceId = 1 },
            new Clip(4, "Start of part 2", 0, 64, Color: ClipColor.Rose) { SourceId = 2 },
            new Clip(5, "Q&A highlights", 520, 612.36, IsIncluded: false, Color: ClipColor.Lime) { SourceId = 2 },
            new Clip(6, "Outro", 640, 760, Color: ClipColor.Cyan) { SourceId = 3 },
        ]) { LastSourceId = 3 };
        editor.Claude.Log.Clear();
        editor.LoadProject(project, new Dictionary<int, Services.IMediaPreview>
        {
            [1] = new DesignSample(variant: 0),
            [2] = new DesignSample(audioStreams: 2, variant: 1),
            [3] = new DesignSample(variant: 2),
        }, DesignSample.SourceInfo.Replace("interview_final_v3.mp4", "keynote_part1.mp4", StringComparison.Ordinal));
        editor.Select(editor.Find(4));
        editor.Time = DesignSample.SampleDuration + 30;
        editor.Claude.Recount();
    }
}
