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

/// <summary>Drives the editor window with headless keyboard and mouse input.</summary>
public class EditorInteractionTests
{
    private static (MainWindow Window, EditorViewModel Editor) Open(DesignScreen screen = DesignScreen.Editing)
    {
        var editor = App.CreateEditor(screen);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Pump();
        return (window, editor);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        window.KeyPress(key, mods, PhysicalKey.None, null);
        window.KeyRelease(key, mods, PhysicalKey.None, null);
        Pump();
    }

    private static void Click(Window window, Point p, RawInputModifiers mods = RawInputModifiers.None)
    {
        window.MouseDown(p, MouseButton.Left, mods);
        window.MouseUp(p, MouseButton.Left, mods);
        Pump();
    }

    [AvaloniaFact]
    public void Space_toggles_playback()
    {
        var (window, editor) = Open();
        Press(window, Key.Space);
        Assert.True(editor.IsPlaying);
        Press(window, Key.Space);
        Assert.False(editor.IsPlaying);
    }

    [AvaloniaFact]
    public void Arrows_step_one_frame_and_shift_arrows_one_second()
    {
        var (window, editor) = Open();
        double t0 = editor.Time;
        Press(window, Key.Right);
        Assert.Equal(t0 + 1 / 29.97, editor.Time, 6);
        Press(window, Key.Left, RawInputModifiers.Shift);
        Assert.Equal(t0 + 1 / 29.97 - 1, editor.Time, 6);
    }

    private static Point Center(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    /// <summary>The clip list row of <paramref name="clip"/>.</summary>
    private static Border Row(Window window, ClipViewModel clip) =>
        window.GetVisualDescendants().OfType<ClipsPanel>().Single().GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("row") && ReferenceEquals(b.DataContext, clip));

    [AvaloniaFact]
    public void E_excludes_and_keeps_the_selected_clip_without_deleting_it()
    {
        var (window, editor) = Open();
        var clip = editor.SelectedClip!;
        int count = editor.Clips.Count;

        Press(window, Key.E);
        Assert.False(clip.IsIncluded);
        Assert.Equal("Keep", editor.ExcludeLabel);
        Press(window, Key.E);
        Assert.True(clip.IsIncluded);
        Assert.Equal(count, editor.Clips.Count);
    }

    [AvaloniaFact]
    public void Del_removes_the_selected_clip()
    {
        var (window, editor) = Open();
        var clip = editor.SelectedClip!;
        Press(window, Key.Delete);
        Assert.DoesNotContain(clip, editor.Clips);
        Assert.NotNull(editor.SelectedClip);
    }

    [AvaloniaFact]
    public void V_picks_the_select_tool()
    {
        var (window, editor) = Open();
        editor.Tool = TimelineTool.Split;
        Press(window, Key.V);
        Assert.True(editor.IsSelectTool);
    }

    [AvaloniaFact]
    public void S_splits_the_clip_at_the_playhead()
    {
        var (window, editor) = Open();
        var clip = editor.SelectedClip!;
        double end = clip.End, t = editor.Time;

        Press(window, Key.S);

        Assert.Equal(6, editor.Clips.Count);
        Assert.Equal(t, clip.End);
        var second = editor.SelectedClip!;
        Assert.Equal("Setup walkthrough (b)", second.Label);
        Assert.Equal(t, second.Start);
        Assert.Equal(end, second.End);
        Assert.Equal(editor.Clips.IndexOf(clip) + 1, editor.Clips.IndexOf(second));
    }

    [AvaloniaFact]
    public void I_and_O_move_the_selected_clips_in_and_out_points()
    {
        var (window, editor) = Open();
        var clip = editor.SelectedClip!;
        editor.SetTime(100);
        Press(window, Key.I);
        Assert.Equal(100, clip.Start);
        editor.SetTime(200);
        Press(window, Key.O);
        Assert.Equal(200, clip.End);
    }

    [AvaloniaFact]
    public void I_outside_a_clip_starts_a_new_ten_second_clip()
    {
        var (window, editor) = Open();
        editor.Select(null);
        editor.SetTime(100);
        Press(window, Key.I);
        Assert.Equal(6, editor.Clips.Count);
        Assert.Equal(100, editor.SelectedClip!.Start);
        Assert.Equal(110, editor.SelectedClip.End);
    }

    [AvaloniaFact]
    public void Ctrl_Z_and_Ctrl_Y_undo_and_redo()
    {
        var (window, editor) = Open();
        var clip = editor.SelectedClip!;
        Press(window, Key.E);
        Assert.False(clip.IsIncluded);
        Press(window, Key.Z, RawInputModifiers.Control);
        Assert.True(clip.IsIncluded);
        Press(window, Key.Y, RawInputModifiers.Control);
        Assert.False(clip.IsIncluded);
        Press(window, Key.Z, RawInputModifiers.Control);
        Press(window, Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.False(clip.IsIncluded);
    }

    [AvaloniaFact]
    public void Shift_Delete_removes_the_selected_clip()
    {
        var (window, editor) = Open();
        var clip = editor.SelectedClip!;
        Press(window, Key.Delete, RawInputModifiers.Shift);
        Assert.DoesNotContain(clip, editor.Clips);
        Assert.Null(editor.Session.Project.Find(clip.Id));
    }

    [AvaloniaFact]
    public void Dragging_a_trim_handle_is_one_undo_step()
    {
        var (window, editor) = Open();
        editor.SnapToKeyframes = false;
        var clip = editor.SelectedClip!;
        double start = clip.Start;
        var timeline = window.GetVisualDescendants().OfType<TimelineControl>().Single();
        var origin = timeline.TranslatePoint(new Point(0, 0), window)!.Value;
        double pps = timeline.Bounds.Width / editor.Duration;
        // The in-handle straddles the clip's left edge, halfway down the tracks.
        var handle = new Point(origin.X + clip.Start * pps + 1, origin.Y + 90);

        window.MouseDown(handle, MouseButton.Left, RawInputModifiers.None);
        for (int dx = 5; dx <= 30; dx += 5)
            window.MouseMove(handle + new Point(dx, 0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(handle + new Point(30, 0), MouseButton.Left, RawInputModifiers.None);
        Pump();

        Assert.Equal(start + 30 / pps, clip.Start, 3);
        Assert.Single(editor.Session.History.Entries);
        editor.Undo();
        Assert.Equal(start, clip.Start);
    }

    [AvaloniaFact]
    public void Ctrl_E_opens_export_and_Escape_closes_it()
    {
        var (window, editor) = Open();
        Press(window, Key.E, RawInputModifiers.Control);
        Assert.True(editor.Export.IsConfiguring);
        Press(window, Key.Escape);
        Assert.False(editor.Export.IsDialogOpen);
    }

    [AvaloniaFact]
    public void Shortcuts_do_nothing_without_a_file()
    {
        var (window, editor) = Open(DesignScreen.Empty);
        Press(window, Key.Space);
        Press(window, Key.E, RawInputModifiers.Control);
        Assert.False(editor.IsPlaying);
        Assert.False(editor.Export.IsDialogOpen);
    }

    [AvaloniaFact]
    public void Clicking_a_clip_row_selects_it_and_moves_the_playhead_into_it()
    {
        var (window, editor) = Open();
        var intro = editor.Clips[0];
        Click(window, Center(window, Row(window, intro)));
        Assert.Same(intro, editor.SelectedClip);
        Assert.Equal(intro.Start, editor.Time);
    }

    [AvaloniaFact]
    public void Clicking_the_switch_toggles_inclusion()
    {
        var (window, editor) = Open();
        var intro = editor.Clips[0];
        var toggle = Row(window, intro).GetVisualDescendants().OfType<CheckBox>().Single();
        Click(window, Center(window, toggle));
        Assert.False(intro.IsIncluded);
        Assert.NotSame(intro, editor.SelectedClip);
    }

    [AvaloniaFact]
    public void Clicking_the_cross_removes_the_clip()
    {
        var (window, editor) = Open();
        var intro = editor.Clips[0];
        var remove = Row(window, intro).GetVisualDescendants().OfType<Button>().Single(b => b is not CheckBox);
        Click(window, Center(window, remove));
        Assert.DoesNotContain(intro, editor.Clips);
        Assert.Equal(4, editor.Clips.Count);
    }

    [AvaloniaFact]
    public void Dragging_a_clip_row_reorders_the_output()
    {
        var (window, editor) = Open();
        var intro = editor.Clips[0];
        var from = Center(window, Row(window, intro));
        var to = Center(window, Row(window, editor.Clips[2]));
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(from + new Point(0, 20), RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        Pump();
        Assert.True(intro.IsDragSource);
        window.MouseUp(to, MouseButton.Left, RawInputModifiers.None);
        Pump();

        Assert.Equal(2, editor.Clips.IndexOf(intro));
        Assert.Equal(3, intro.Number);
        Assert.False(intro.IsDragSource);
    }

    [AvaloniaFact]
    public void Clicking_the_timeline_moves_the_playhead_and_selects_the_clip_under_it()
    {
        var (window, editor) = Open();
        var timeline = window.GetVisualDescendants().OfType<TimelineControl>().Single();
        var origin = timeline.TranslatePoint(new Point(0, 0), window)!.Value;
        double t = 150;
        double x = origin.X + t / editor.Duration * timeline.Bounds.Width;

        Click(window, new Point(x, origin.Y + 60));

        Assert.Equal(t, editor.Time, 0);
        Assert.Equal("Setup walkthrough", editor.SelectedClip!.Label);
    }

    [AvaloniaFact]
    public void With_the_split_tool_clicking_a_clip_splits_it_there()
    {
        var (window, editor) = Open();
        var demo = editor.Clips.Single(c => c.Label == "Export demo");
        var timeline = window.GetVisualDescendants().OfType<TimelineControl>().Single();
        var origin = timeline.TranslatePoint(new Point(0, 0), window)!.Value;
        double x = origin.X + 300 / editor.Duration * timeline.Bounds.Width;
        editor.SplitToolCommand.Execute(null);
        Pump();

        Click(window, new Point(x, origin.Y + 60));

        Assert.Equal(6, editor.Clips.Count);
        Assert.Equal(300, demo.End, 0);
        Assert.Equal(300, editor.Time, 0);
        Assert.Equal(editor.Clips.IndexOf(demo) + 1, editor.Clips.IndexOf(editor.SelectedClip!));
    }

    [AvaloniaFact]
    public void Escape_closes_the_settings()
    {
        var (window, editor) = Open(DesignScreen.Settings);
        Assert.True(editor.Settings.IsOpen);
        Press(window, Key.Space);
        Assert.False(editor.IsPlaying);
        Press(window, Key.Escape);
        Assert.False(editor.Settings.IsOpen);
    }

    [AvaloniaFact]
    public void Collapsing_the_claude_panel_gives_the_clip_list_the_space()
    {
        var (window, editor) = Open();
        var panel = window.GetVisualDescendants().OfType<ClaudePanel>().Single();
        var list = window.GetVisualDescendants().OfType<ClipsPanel>().Single();
        Assert.False(editor.Claude.IsOpen);
        Assert.Equal(39, panel.Bounds.Height);
        double listClosed = list.Bounds.Height;

        editor.Claude.ToggleOpenCommand.Execute(null);
        Pump();
        Assert.InRange(panel.Bounds.Height, 100, 300);
        Assert.Equal(listClosed - (panel.Bounds.Height - 39), list.Bounds.Height, 1);
    }
}
