using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.Demo;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;

namespace HighlightCut.App.Tests;

/// <summary>Screenshots of Settings → Keyboard at the design size (setKeys, setKeysRec, setKeysConflict, no match).</summary>
public class KeyboardScreensTests
{
    [AvaloniaTheory]
    [InlineData(DesignScreen.SettingsKeyboard, "", null)]
    [InlineData(DesignScreen.SettingsKeyboardRecording, "", null)]
    [InlineData(DesignScreen.SettingsKeyboardConflict, "", null)]
    [InlineData(DesignScreen.SettingsKeyboard, "xyz", "settings-keyboard-no-match")]
    public void Keyboard_screen_renders_at_design_size(DesignScreen screen, string query, string? file)
    {
        var editor = App.CreateEditor(screen);
        editor.Settings.Keyboard.Query = query;
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        // Twice: the active row is focused and scrolled into view by a posted job.
        for (int i = 0; i < 2; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        var keyboard = editor.Settings.Keyboard;
        if (keyboard.Selected is { } selected)
        {
            var dialog = window.GetVisualDescendants().OfType<SettingsDialog>().Single();
            var scroller = dialog.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroll");
            var row = dialog.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("kbrow") && b.DataContext == selected);
            double bottom = row.TranslatePoint(new Point(0, row.Bounds.Height), scroller)!.Value.Y;
            Assert.InRange(bottom, 0, scroller.Viewport.Height + 0.5);
            Assert.Same(row, window.FocusManager?.GetFocusedElement());
        }
        if (query.Length > 0)
            Assert.True(keyboard.IsEmpty);

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(1440, 900), frame!.PixelSize);
        // The design screens themselves are saved by DesignScreensTests.
        if (file is not null)
            frame.Save(Path.Combine(Screenshots.Directory, file + ".png"), new PngBitmapEncoderOptions());
        window.Close();
    }

    [AvaloniaFact]
    public void A_replaced_shortcut_shows_changed_and_not_set()
    {
        var editor = App.CreateEditor(DesignScreen.SettingsKeyboardConflict);
        var keyboard = editor.Settings.Keyboard;
        keyboard.Row(ShortcutAction.ToggleExclude).ReplaceCommand.Execute(null);
        keyboard.Query = "ex";
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var row = window.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("kbrow") && b.DataContext == keyboard.Row(ShortcutAction.Export));
        var texts = row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Equal(["Export", "changed", "Not set"], texts);

        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(Screenshots.Directory, "settings-keyboard-changed.png"), new PngBitmapEncoderOptions());
        window.Close();
    }

    [AvaloniaFact]
    public void Only_the_chosen_row_shows_its_state()
    {
        var editor = App.CreateEditor(DesignScreen.SettingsKeyboardConflict);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var rows = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("kbrow")).ToList();
        Assert.Equal(KeyMap.Catalog.Count, rows.Count);
        var selected = Assert.Single(rows, r => r.Classes.Contains("sel"));
        Assert.Equal(ShortcutAction.ToggleExclude, ((ShortcutRowViewModel)selected.DataContext!).Action);
        var texts = selected.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Ctrl E", texts);
        Assert.Contains("Esc cancels", texts);
        Assert.Contains("Ctrl+E is used by Export.", texts);
        Assert.DoesNotContain("Press the new shortcut…", texts);
        window.Close();
    }
}
