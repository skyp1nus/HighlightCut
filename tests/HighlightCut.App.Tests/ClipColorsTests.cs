using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.Controls;
using HighlightCut.App.Demo;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;
using HighlightCut.Core.Model;

namespace HighlightCut.App.Tests;

/// <summary>Clip colours: the swatch in the clip list and its picker, the context menu, and the timeline segments.</summary>
public class ClipColorsTests
{
    private static (MainWindow Window, EditorViewModel Editor) Open()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
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

    private static void Click(TopLevel root, Point p)
    {
        root.MouseDown(p, MouseButton.Left);
        root.MouseUp(p, MouseButton.Left);
        Pump();
    }

    private static Point Center(Visual root, Visual control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;

    private static Button Swatch(Window window, ClipViewModel clip) =>
        window.GetVisualDescendants().OfType<ClipsPanel>().Single().GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("swatch") && ReferenceEquals(b.DataContext, clip));

    [AvaloniaFact]
    public void Every_row_shows_its_clips_colour()
    {
        var (window, editor) = Open();

        foreach (var clip in editor.Clips)
        {
            var swatch = Swatch(window, clip);
            Assert.Equal(ClipBrushes.ColorOf(clip.Color), ((ISolidColorBrush)swatch.Background!).Color);
            Assert.Equal("Colour: " + clip.Color, ToolTip.GetTip(swatch));
        }
        Assert.Equal(editor.Clips.Count, editor.Clips.Select(c => c.Color).Distinct().Count());
        window.Close();
    }

    [AvaloniaFact]
    public void The_swatch_opens_a_picker_that_recolours_the_clip_as_one_undo_step()
    {
        var (window, editor) = Open();
        var clip = editor.Clips[1];
        var before = clip.Color;
        var swatch = Swatch(window, clip);

        Click(window, Center(window, swatch));
        var flyout = Assert.IsType<Flyout>(swatch.Flyout);
        Assert.True(flyout.IsOpen);
        var buttons = ((Control)flyout.Content!).GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("pick")).ToList();
        Assert.Equal(ClipPalette.Colors.Count, buttons.Count);
        Assert.Single(buttons, b => b.Classes.Contains("current"));
        // The picker is a popup of its own: it is clicked, and photographed, there.
        var popup = TopLevel.GetTopLevel(buttons[0])!;
        using (var shot = popup.CaptureRenderedFrame())
            shot!.Save(Path.Combine(Screenshots.Directory, "clip-colour-picker.png"), new PngBitmapEncoderOptions());

        var pink = buttons.Single(b => ((ClipColorChoice)b.DataContext!).Color == ClipColor.Pink);
        Click(popup, Center(popup, pink));

        Assert.False(flyout.IsOpen);
        Assert.Equal(ClipColor.Pink, clip.Color);
        Assert.Equal(ClipColor.Pink, editor.Session.Project.Get(clip.Id).Color);
        Assert.Equal("Coloured clip 2 pink", editor.Session.History.Entries[^1].Description);

        editor.Undo();
        Assert.Equal(before, clip.Color);
        window.Close();
    }

    [AvaloniaFact]
    public void The_context_menu_has_the_palette_for_the_selected_clip()
    {
        var (window, editor) = Open();
        var list = window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "ClipList");
        var menu = list.ContextMenu!.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Colour");

        editor.Select(editor.Clips[0]);
        list.ContextMenu.Open(list);
        Pump();
        var choices = Assert.IsAssignableFrom<IReadOnlyList<ClipColorChoice>>(menu.ItemsSource);
        Assert.Equal(ClipPalette.Colors, choices.Select(c => c.Color));
        Assert.True(choices.Single(c => c.IsCurrent).Color == editor.Clips[0].Color);

        choices.Single(c => c.Color == ClipColor.Indigo).Pick.Execute(null);
        Assert.Equal(ClipColor.Indigo, editor.Clips[0].Color);
        Assert.Contains(((IReadOnlyList<ClipColorChoice>)menu.ItemsSource!), c => c.IsCurrent && c.Color == ClipColor.Indigo);

        editor.Select(null);
        Pump();
        Assert.False(menu.IsEnabled);
        list.ContextMenu.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void Timeline_segments_carry_a_stripe_of_their_colour()
    {
        var (window, editor) = Open();
        var timeline = window.GetVisualDescendants().OfType<TimelineControl>().Single();
        editor.Select(null);
        Pump();

        using var frame = window.CaptureRenderedFrame()!;
        foreach (var clip in editor.Clips.Where(c => c.IsIncluded))
        {
            double x = (clip.Start + clip.End) / 2 / editor.Duration * timeline.ExtentWidth - timeline.ScrollOffset;
            var p = timeline.TranslatePoint(new Point(x, TimelineControl.VideoTop + 3.5), window)!.Value;
            Assert.Equal(ClipBrushes.ColorOf(clip.Color), PixelAt(frame, p), ColorComparer);
        }
        frame.Save(Path.Combine(Screenshots.Directory, "clip-colours.png"), new PngBitmapEncoderOptions());
        window.Close();
    }

    [AvaloniaFact]
    public void A_split_shows_two_colours_and_the_next_clip_number()
    {
        var (window, editor) = Open();
        var clip = editor.Clips[2];
        editor.SplitAt(clip, (clip.Start + clip.End) / 2);
        Pump();

        var second = editor.SelectedClip!;
        Assert.Equal("Clip 6", second.Label);
        Assert.NotEqual(clip.Color, second.Color);
        Assert.NotEqual(editor.Clips[editor.Clips.IndexOf(second) + 1].Color, second.Color);
        Assert.Equal(ClipBrushes.ColorOf(second.Color), ((ISolidColorBrush)Swatch(window, second).Background!).Color);
        window.Close();
    }

    private static readonly IEqualityComparer<Color> ColorComparer = EqualityComparer<Color>.Create(
        (a, b) => Math.Abs(a.R - b.R) <= 3 && Math.Abs(a.G - b.G) <= 3 && Math.Abs(a.B - b.B) <= 3, c => 0);

    private static Color PixelAt(WriteableBitmap frame, Point p)
    {
        using var fb = frame.Lock();
        int x = (int)p.X, y = (int)p.Y;
        int bgra = Marshal.ReadInt32(fb.Address, y * fb.RowBytes + x * 4);
        var c = Color.FromUInt32((uint)bgra);
        return fb.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? Color.FromRgb(c.B, c.G, c.R) : Color.FromRgb(c.R, c.G, c.B);
    }
}
