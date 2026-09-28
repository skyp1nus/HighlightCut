using Avalonia;
using Avalonia.Media;
using OurCut.App.Controls;
using OurCut.App.Services;
using OurCut.Core.Model;

namespace OurCut.App.Demo;

/// <summary>
/// The sample project from design/project/OurCut.dc.html ("interview_final_v3.mp4"): its clips,
/// silence and scene markers, a keyframe every 4 s and the prototype's generated waveform, so demo
/// screenshots can be compared with the design.
/// </summary>
public sealed partial class DesignSample : IMediaPreview
{
    public const double SampleDuration = 872.48;

    /// <summary>Bars in the prototype's waveform (<c>getBars</c>, N = 440).</summary>
    public const int WaveformBars = 440;

    private const double KeyframeInterval = 4;

    private static readonly TimeRange[] SilenceRanges =
    [
        new(40.8, 45.2), new(60, 72), new(118.4, 124), new(205, 214), new(300, 306),
        new(331, 337), new(420, 470), new(690, 700), new(822, 828.8),
    ];

    private static readonly double[] Scenes = [118.4, 262.08, 410, 520, 640, 750];

    /// <summary>Thumbnail stripe colours per scene (<c>tones</c> in the prototype).</summary>
    private static readonly (Color A, Color B)[] Tones =
    [
        (Color.Parse("#17181b"), Color.Parse("#1b1c20")), (Color.Parse("#1c1d21"), Color.Parse("#202125")),
        (Color.Parse("#15161a"), Color.Parse("#191a1e")), (Color.Parse("#1b1c1e"), Color.Parse("#1f2022")),
        (Color.Parse("#18191d"), Color.Parse("#1d1e22")), (Color.Parse("#161719"), Color.Parse("#1a1b1d")),
        (Color.Parse("#1a1b1f"), Color.Parse("#1e1f23")),
    ];

    private static readonly Color PlayerA = Color.Parse("#121315");
    private static readonly Color PlayerB = Color.Parse("#15161a");
    private static readonly IBrush PlayerText = new SolidColorBrush(Color.Parse("#71717a"));

    private readonly double[] _keyframes;

    public DesignSample()
    {
        var kf = new List<double>();
        for (double t = 0; t <= SampleDuration; t += KeyframeInterval)
            kf.Add(t);
        _keyframes = [.. kf];
    }

    /// <summary>The sample source file: a 14:32 interview with one stereo audio track.</summary>
    public static SourceMedia Source { get; } = new("interview_final_v3.mp4", SampleDuration, 29.97,
        [new AudioTrack(1, "Stereo")]);

    /// <summary>Media details for the sample file (status bar).</summary>
    public const string SourceInfo = "interview_final_v3.mp4  ·  H.264 High  ·  1920×1080  ·  29.97 fps  ·  AAC 48 kHz stereo  ·  1.62 GB";

    /// <summary>The design's five clips (Q&amp;A highlights excluded), in output order.</summary>
    public static Project Project { get; } = new("interview_final_v3", Source,
    [
        new Clip(1, "Cold open", 12, 45.2, Color: ClipColor.Teal),
        new Clip(2, "Setup walkthrough", 118.4, 190, Color: ClipColor.Amber),
        new Clip(3, "Export demo", 262.08, 365.52, Color: ClipColor.Violet),
        new Clip(4, "Q&A highlights", 520, 612.36, IsIncluded: false, Color: ClipColor.Rose),
        new Clip(5, "Outro", 750, 828.8, Color: ClipColor.Lime),
    ]);

    public double Duration => SampleDuration;
    public double FrameRate => 29.97;
    public IReadOnlyList<double> Keyframes => _keyframes;
    public IReadOnlyList<TimeRange> Silences => SilenceRanges;
    public IReadOnlyList<double> SceneChanges => Scenes;
    public int AudioStreamCount => 1;

    /// <summary>The sample draws its own placeholder picture (the player's overlay is not needed).</summary>
    public bool IsPlaceholder => false;

    /// <summary>There is no file behind the sample; its playback is simulated.</summary>
    public bool IsPlayable => false;

    public string? Activity => null;
    public string? AnalysisError => null;

    /// <summary>The prototype's bar height (0..1) for bar <paramref name="i"/> of <see cref="WaveformBars"/>.</summary>
    public static double BarLevel(int i)
    {
        double t = (i + 0.5) / WaveformBars * SampleDuration;
        if (SilenceRanges.Any(r => t >= r.Start && t <= r.End))
            return 0.05;
        double r = Math.Abs(Math.Sin(i * 12.9898) * 43758.5453) % 1;
        return Math.Min(0.95, 0.14 + 0.5 * Math.Abs(Math.Sin(i * 0.37) * Math.Cos(i * 0.113)) + 0.3 * r);
    }

    /// <summary>Level of the design's bar at the middle of the range.</summary>
    public double AudioPeak(int stream, double startTime, double endTime)
    {
        int i = (int)Math.Floor((startTime + endTime) / 2 / SampleDuration * WaveformBars);
        return BarLevel(Math.Clamp(i, 0, WaveformBars - 1));
    }

    public void DrawFrame(DrawingContext context, Rect rect, double time, FrameLook look, int variant)
    {
        if (look == FrameLook.Player)
        {
            Stripes.Draw(context, rect, PlayerA, PlayerB, 10);
            var text = new FormattedText("video frame · 1920×1080", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(new FontFamily(FontSetup.Mono)), 11, PlayerText);
            context.DrawText(text, new Point(rect.X + (rect.Width - text.Width) / 2, rect.Y + (rect.Height - text.Height) / 2));
            return;
        }
        var (a, b) = Tones[Scenes.Count(x => x <= time) % Tones.Length];
        Stripes.Draw(context, rect, a, b, 6);
    }
}
