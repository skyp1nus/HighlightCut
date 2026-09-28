using OurCut.Core.Model;

namespace OurCut.Core.Tests;

/// <summary>The sample project from the design: a 14:32 keynote with six clips, one excluded.</summary>
internal static class Sample
{
    public static SourceMedia Source { get; } = new("/videos/keynote_final_4k.mp4", 872.48, 29.97,
        [new AudioTrack(1, "Mic"), new AudioTrack(2, "System"), new AudioTrack(3, "Music")]);

    public static Project Project { get; } = new("launch-keynote", Source,
    [
        new Clip(1, "Intro", 12.04, 45.32, Color: ClipColor.Teal),
        new Clip(2, "Setup", 118.6, 190.12, Color: ClipColor.Amber),
        new Clip(3, "Demo — import", 242.88, 404.0, Color: ClipColor.Violet),
        new Clip(4, "Demo — trim", 495.2, 602.56, Color: ClipColor.Rose),
        new Clip(6, "Q&A", 640.0, 728.4, IsIncluded: false, Color: ClipColor.Orange),
        new Clip(5, "Outro", 750.0, 828.72, Color: ClipColor.Lime),
    ]);
}
