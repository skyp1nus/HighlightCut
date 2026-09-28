using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.Controls;
using HighlightCut.App.Demo;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;

namespace HighlightCut.App.Tests;

/// <summary>Dragging a trim handle next to another clip: the magnet, the stop at the neighbour, and joining.</summary>
public class TimelineMagnetTests
{
    private static (MainWindow Window, EditorViewModel Editor, TimelineControl Timeline) Open()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
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

    private static Point At(Window window, TimelineControl timeline, EditorViewModel editor, double t, double y = 90)
    {
        double x = t / editor.Duration * timeline.ExtentWidth - timeline.ScrollOffset;
        return timeline.TranslatePoint(new Point(x, y), window)!.Value;
    }

    /// <summary>Clip 2 and the clip that ends last before it (with a gap between them), and pixels per second.</summary>
    private static (ClipViewModel Before, ClipViewModel Clip, double Pps) Neighbours(EditorViewModel editor, TimelineControl timeline)
    {
        var clip = editor.Find(2)!;
        var before = editor.Clips.Where(c => c.End <= clip.Start).MaxBy(c => c.End)!;
        double pps = timeline.ExtentWidth / editor.Duration;
        Assert.True((clip.Start - before.End) * pps > 40);
        return (before, clip, pps);
    }

    /// <summary>Drags the in-handle of <paramref name="clip"/> to time <paramref name="to"/> and lets go.</summary>
    private static void DragIn(Window window, TimelineControl timeline, EditorViewModel editor, ClipViewModel clip, double to,
        RawInputModifiers modifiers = RawInputModifiers.None, bool release = true)
    {
        var from = At(window, timeline, editor, clip.Start);
        window.MouseDown(from, MouseButton.Left, modifiers);
        window.MouseMove(At(window, timeline, editor, to), RawInputModifiers.LeftMouseButton | modifiers);
        if (release)
            window.MouseUp(At(window, timeline, editor, to), MouseButton.Left, modifiers);
        Pump();
    }

    [AvaloniaFact]
    public void Dragging_an_in_point_near_the_clip_before_snaps_it_onto_that_clips_end()
    {
        var (window, editor, timeline) = Open();
        var (before, clip, pps) = Neighbours(editor, timeline);
        editor.SnapToKeyframes = false;

        // 5 px short of the neighbour: within the snap distance, and the Snap chip is off.
        DragIn(window, timeline, editor, clip, before.End + 5 / pps, release: false);
        Assert.Equal(before.End, clip.Start);
        window.MouseUp(At(window, timeline, editor, clip.Start), MouseButton.Left);
        Pump();

        Assert.Empty(editor.Session.Project.Overlaps());
        Assert.Single(editor.Session.History.Entries);
        window.Close();
    }

    [AvaloniaFact]
    public void Alt_drags_without_the_magnet()
    {
        var (window, editor, timeline) = Open();
        var (before, clip, pps) = Neighbours(editor, timeline);

        DragIn(window, timeline, editor, clip, before.End + 5 / pps, RawInputModifiers.Alt);

        Assert.Equal(before.End + 5 / pps, clip.Start, 1);
        Assert.True(clip.Start > before.End);
        window.Close();
    }

    [AvaloniaFact]
    public void Dragging_into_the_clip_before_stops_at_its_edge()
    {
        var (window, editor, timeline) = Open();
        var (before, clip, _) = Neighbours(editor, timeline);
        double beforeStart = before.Start, beforeEnd = before.End;

        DragIn(window, timeline, editor, clip, (before.Start + before.End) / 2, RawInputModifiers.Alt);

        Assert.Equal(beforeEnd, clip.Start);
        Assert.Equal((beforeStart, beforeEnd), (before.Start, before.End));
        Assert.Empty(editor.Session.Project.Overlaps());
        window.Close();
    }

    [AvaloniaFact]
    public void Snapped_clips_join_into_one_with_one_undo_step()
    {
        var (window, editor, timeline) = Open();
        var (before, clip, pps) = Neighbours(editor, timeline);
        DragIn(window, timeline, editor, clip, before.End + 3 / pps);
        editor.Select(before);
        Assert.True(editor.CanJoinWithNext);
        int count = editor.Clips.Count;
        string label = before.Label;
        double end = clip.End;

        editor.JoinWithNext();

        Assert.Equal(count - 1, editor.Clips.Count);
        Assert.Null(editor.Find(2));
        Assert.Same(before, editor.SelectedClip);
        Assert.Equal((label, end), (before.Label, before.End));

        editor.Undo();
        Assert.Equal(count, editor.Clips.Count);
        Assert.Equal(before.End, editor.Find(2)!.Start);
        window.Close();
    }

    [AvaloniaFact]
    public void A_right_click_on_a_clip_selects_it_for_the_context_menu()
    {
        var (window, editor, timeline) = Open();
        var clip = editor.Find(2)!;
        editor.Select(null);

        window.MouseDown(At(window, timeline, editor, (clip.Start + clip.End) / 2, 60), MouseButton.Right);
        window.MouseUp(At(window, timeline, editor, (clip.Start + clip.End) / 2, 60), MouseButton.Right);
        Pump();

        Assert.Same(clip, editor.SelectedClip);
        Assert.NotNull(timeline.ContextMenu);
        window.Close();
    }
}
