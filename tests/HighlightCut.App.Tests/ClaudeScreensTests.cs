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

/// <summary>The Claude export screens at the design size, saved to artifacts/screenshots/.</summary>
public class ClaudeScreensTests
{
    [AvaloniaTheory]
    [InlineData(DesignScreen.ClaudeRequest)]
    [InlineData(DesignScreen.ClaudeExporting)]
    [InlineData(DesignScreen.ClaudeExportFailed)]
    public void Claude_screen_shows_the_card_without_the_dialog(DesignScreen screen)
    {
        var editor = App.CreateEditor(screen);
        var window = Show(editor);

        var banner = window.GetVisualDescendants().OfType<ClaudeRequestBanner>().Single();
        Assert.Equal(screen == DesignScreen.ClaudeRequest, IsShown(banner.GetVisualDescendants().OfType<Border>().First()));
        var card = window.GetVisualDescendants().OfType<ClaudeExportCard>().Single();
        Assert.True(IsShown(card.GetVisualDescendants().OfType<Border>().First()));
        Assert.False(IsShown(window.GetVisualDescendants().OfType<ExportDialog>().Single()));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("denied", "Export denied")]
    [InlineData("cancelled", "Export cancelled")]
    [InlineData("done", "Exported keynote-cut.mp4")]
    public void The_card_shows_how_Claudes_export_ended(string end, string title)
    {
        var editor = App.CreateEditor(end == "denied" ? DesignScreen.ClaudeRequest : DesignScreen.ClaudeExporting);
        var card = editor.Claude.Export;
        switch (end)
        {
            case "denied":
                card.DenyCommand.Execute(null);
                break;
            case "cancelled":
                card.CancelCommand.Execute(null);
                break;
            default:
                editor.Export.Progress = 1;
                break;
        }
        var window = Show(editor);

        Assert.Equal(title, card.Title);
        Assert.False(IsShown(window.GetVisualDescendants().OfType<ExportDialog>().Single()));
        Assert.Contains(window.GetVisualDescendants().OfType<ClaudeExportCard>().Single().GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == title);
        Save(window, $"claude-export-{end}.png");
    }

    [AvaloniaFact]
    public void The_banner_sits_centred_over_the_preview()
    {
        var editor = App.CreateEditor(DesignScreen.ClaudeRequest);
        var window = Show(editor);

        var box = window.GetVisualDescendants().OfType<ClaudeRequestBanner>().Single();
        var player = window.GetVisualDescendants().OfType<PlayerPanel>().Single();
        var at = box.TranslatePoint(new Point(0, 0), player)!.Value;

        Assert.Equal(680, box.Bounds.Width, 1);
        Assert.Equal(12, at.Y, 1);
        Assert.Equal((player.Bounds.Width - 680) / 2, at.X, 1);
        window.Close();
    }

    [AvaloniaFact]
    public void The_pill_shows_the_running_export()
    {
        var editor = App.CreateEditor(DesignScreen.ClaudeExporting);
        var window = Show(editor);

        var pill = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ExportPill");
        var strip = pill.GetVisualDescendants().OfType<ProgressBar>().Single();
        Assert.True(strip.IsVisible);
        // The simulated export may have ticked while the window came up.
        Assert.Equal(editor.Export.Progress, strip.Value, 6);
        Assert.InRange(strip.Value, 0.45, 0.5);
        Assert.Contains(pill.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == editor.ExportButtonText);
        Assert.StartsWith("Exporting 4", editor.ExportButtonText, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void The_dialog_opens_over_Claudes_running_export()
    {
        var editor = App.CreateEditor(DesignScreen.ClaudeExporting);
        editor.Export.Open();
        var window = Show(editor);

        Assert.True(IsShown(window.GetVisualDescendants().OfType<ExportDialog>().Single()));
        Assert.StartsWith("Started by Claude · ", editor.Export.ProgressDetail, StringComparison.Ordinal);
        Save(window, "claude-export-dialog.png");
    }

    [AvaloniaFact]
    public void The_badge_is_neutral_while_waiting()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        editor.Claude.IsConnected = false;
        editor.Claude.IsListening = true;
        var window = Show(editor);

        var badge = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "McpBadge");
        Assert.DoesNotContain("on", badge.Classes);
        Assert.Contains(badge.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "MCP · Waiting for Claude");
        Save(window, "mcp-waiting.png");
    }

    private static MainWindow Show(EditorViewModel editor)
    {
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return window;
    }

    private static void Save(Window window, string file)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(1440, frame!.PixelSize.Width);
        Assert.Equal(900, frame.PixelSize.Height);
        frame.Save(Path.Combine(Screenshots.Directory, file), new PngBitmapEncoderOptions());
        window.Close();
    }

    private static bool IsShown(Visual visual) =>
        visual.IsVisible && visual.GetVisualAncestors().All(a => a.IsVisible);
}
