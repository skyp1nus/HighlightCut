using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OurCut.App.Controls;
using OurCut.App.Demo;
using OurCut.App.ViewModels;
using OurCut.App.Views;

namespace OurCut.App.Tests;

/// <summary>A plain click on the timeline moves the playhead to the time under the pointer, and it stays there.</summary>
public class TimelineClickTests
{
    private static (MainWindow Window, EditorViewModel Editor, TimelineControl Timeline) Open(EditorViewModel editor)
    {
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Pump();
        return (window, editor, window.GetVisualDescendants().OfType<TimelineControl>().Single());
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The window point over time <paramref name="t"/>, <paramref name="y"/> down the timeline.</summary>
    private static Point At(Window window, TimelineControl timeline, EditorViewModel editor, double t, double y)
    {
        double x = t / editor.Duration * timeline.ExtentWidth - timeline.ScrollOffset;
        return timeline.TranslatePoint(new Point(x, y), window)!.Value;
    }

    /// <summary>Press and release, the pointer moving <paramref name="shake"/> px in between as a hand does.</summary>
    private static void Click(Window window, Point p, double shake = 0)
    {
        window.MouseDown(p, MouseButton.Left);
        if (shake != 0)
            window.MouseMove(p + new Point(shake, 0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(p + new Point(shake, 0), MouseButton.Left);
        Pump();
    }

    [AvaloniaFact]
    public void Clicking_a_trim_handle_moves_the_playhead_and_selects_its_clip_without_trimming()
    {
        var (window, editor, timeline) = Open(App.CreateEditor(DesignScreen.Editing));
        var clip = editor.Clips.First(c => !c.IsSelected && editor.Clips.All(o => ReferenceEquals(o, c) || Math.Abs(o.End - c.Start) > 5));
        double start = clip.Start, end = clip.End;
        double t = start + 1 / (timeline.ExtentWidth / editor.Duration);

        // The in-handle straddles the clip's left edge, halfway down the tracks; the hand shakes by 2 px.
        Click(window, At(window, timeline, editor, t, 90), shake: 2);

        Assert.Equal(t, editor.Time, 3);
        Assert.Same(clip, editor.SelectedClip);
        Assert.Equal((start, end), (clip.Start, clip.End));
        Assert.Empty(editor.Session.History.Entries);
        window.Close();
    }

    [AvaloniaFact]
    public void A_shaking_click_on_a_track_leaves_the_playhead_where_it_was_pressed()
    {
        var (window, editor, timeline) = Open(App.CreateEditor(DesignScreen.Editing));

        Click(window, At(window, timeline, editor, 150, 60), shake: -3);

        Assert.Equal(150, editor.Time, 3);
        window.Close();
    }

    [AvaloniaFact]
    public void Dragging_past_the_threshold_still_scrubs()
    {
        var (window, editor, timeline) = Open(App.CreateEditor(DesignScreen.Editing));
        var from = At(window, timeline, editor, 150, 60);
        double pps = timeline.ExtentWidth / editor.Duration;

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(20, 0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(from + new Point(20, 0), MouseButton.Left);
        Pump();

        Assert.Equal(150 + 20 / pps, editor.Time, 3);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicks_on_the_ruler_and_the_tracks_land_on_the_clicked_time_when_zoomed_and_scrolled()
    {
        var (window, editor, timeline) = Open(App.CreateEditor(DesignScreen.Editing));
        editor.ZoomLevel = 0.6;
        Pump();
        timeline.ScrollOffset = timeline.MaxScroll / 2;
        Pump();
        double left = timeline.ScrollOffset / timeline.ExtentWidth * editor.Duration;
        double width = timeline.Bounds.Width / timeline.ExtentWidth * editor.Duration;

        foreach (var (t, y) in new[] { (left + width * 0.3, 10.0), (left + width * 0.6, 60.0), (left + width * 0.8, 130.0) })
        {
            Click(window, At(window, timeline, editor, t, y));
            Assert.Equal(t, editor.Time, 2);
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_click_while_playing_stays_on_the_clicked_time_while_mpv_catches_up()
    {
        var player = new FakePlayer();
        var editor = App.CreateEditor(null, new FakeMediaOpener(), player: player);
        await editor.OpenMediaAsync("/videos/fake.mp4");
        var (window, _, timeline) = Open(editor);
        editor.TogglePlay();
        player.Report(2.0, playing: true);
        player.Calls.Clear();

        Click(window, At(window, timeline, editor, 7.5, 60));
        Assert.Equal(7.5, editor.Time, 2);
        Assert.Single(player.Calls, c => c.StartsWith("seek 7.5", StringComparison.Ordinal));

        // mpv keeps playing on from 2 s until it gets to the seek: those positions are stale.
        player.Report(2.04, playing: true, seeking: true);
        player.Report(2.08, playing: true, seeking: true);
        Assert.Equal(7.5, editor.Time, 2);

        // Landed (SeekState holds the target until mpv reports a new position), then playing on from there.
        player.Report(7.5, playing: true);
        player.Report(7.54, playing: true);
        Assert.Equal(7.54, editor.Time, 2);
        Assert.Single(player.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_click_while_paused_stays_on_the_clicked_time()
    {
        var player = new FakePlayer();
        var editor = App.CreateEditor(null, new FakeMediaOpener(), player: player);
        await editor.OpenMediaAsync("/videos/fake.mp4");
        var (window, _, timeline) = Open(editor);
        player.Report(1.0);
        player.Calls.Clear();

        Click(window, At(window, timeline, editor, 4.0, 60));
        player.Report(1.0, seeking: true);
        Assert.Equal(4.0, editor.Time, 2);

        // A second click before the first seek landed.
        Click(window, At(window, timeline, editor, 6.0, 60));
        player.Report(4.0, seeking: true);
        Assert.Equal(6.0, editor.Time, 2);

        player.Report(6.0);
        Assert.Equal(6.0, editor.Time, 2);
        Assert.Equal(2, player.Calls.Count(c => c.StartsWith("seek", StringComparison.Ordinal)));
        window.Close();
    }
}
