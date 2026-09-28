using System.Text.RegularExpressions;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.Controls;
using HighlightCut.App.Demo;
using HighlightCut.App.Views;

namespace HighlightCut.App.Tests;

/// <summary>
/// Renders every screen of the design with its sample data at the design size (1440×900) and
/// saves a screenshot to artifacts/screenshots/ (named as <c>--demo</c> takes it) for comparison with design/project/HighlightCut.dc.html.
/// </summary>
public class DesignScreensTests
{
    public static TheoryData<DesignScreen> Screens { get; } = new(Enum.GetValues<DesignScreen>());

    [AvaloniaTheory]
    [MemberData(nameof(Screens))]
    public void Design_screen_renders_at_design_size(DesignScreen screen)
    {
        var editor = App.CreateEditor(screen);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        // Twice: the keyboard screens focus and scroll their row into view in a posted job.
        for (int i = 0; i < 2; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        // Back to the design's 34 % and 45 % in case the simulated transcription or export ticked meanwhile.
        if (screen == DesignScreen.Transcribing)
        {
            ((DesignSample)editor.Media!).StartTranscribing(0.34);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        if (screen == DesignScreen.ClaudeExporting)
        {
            editor.Export.Progress = 0.45;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        using var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.Equal(1440, frame!.PixelSize.Width);
        Assert.Equal(900, frame.PixelSize.Height);
        frame.Save(Path.Combine(Screenshots.Directory, Screenshots.Name(screen) + ".png"), new PngBitmapEncoderOptions());
        window.Close();
    }

    [Fact]
    public void Screenshot_names_are_the_demo_arguments()
    {
        Assert.Equal("claude-export-failed", Screenshots.Name(DesignScreen.ClaudeExportFailed));
        foreach (var screen in Enum.GetValues<DesignScreen>())
            Assert.Equal(screen, App.ParseDemoScreen(["--demo", Screenshots.Name(screen)]));
    }
}

public class TimelineZoomTests
{
    [AvaloniaFact]
    public void Zooming_in_keeps_the_playhead_in_view_and_enables_scrolling()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var timeline = window.GetVisualDescendants().OfType<TimelineControl>().Single();

        editor.ZoomLevel = 0.5;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(timeline.MaxScroll > 0);
        double playheadX = editor.Time / editor.Duration * timeline.ExtentWidth - timeline.ScrollOffset;
        Assert.InRange(playheadX, 0, timeline.Bounds.Width);
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(Screenshots.Directory, "editing-zoomed.png"), new PngBitmapEncoderOptions());
        window.Close();
    }
}

internal static partial class Screenshots
{
    public static string Directory { get; } = Create();

    /// <summary>"claude-export-failed" for <see cref="DesignScreen.ClaudeExportFailed"/>.</summary>
    public static string Name(DesignScreen screen) => WordStart().Replace(screen.ToString(), "-$1").ToLowerInvariant();

    [GeneratedRegex("(?<=[a-z])([A-Z])")]
    private static partial Regex WordStart();

    private static string Create()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HighlightCut.slnx")))
            dir = dir.Parent;
        string root = dir?.FullName ?? AppContext.BaseDirectory;
        string path = Path.Combine(root, "artifacts", "screenshots");
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
}
