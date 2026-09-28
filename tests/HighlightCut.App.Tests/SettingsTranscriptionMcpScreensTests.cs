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

/// <summary>
/// Settings → Transcription and MCP server in the states the design draws, saved to artifacts/screenshots/ for comparison
/// with design/project/HighlightCut.dc.html.
/// </summary>
public class SettingsTranscriptionMcpScreensTests
{
    private static (MainWindow Window, EditorViewModel Editor) Open(DesignScreen screen)
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

    private static void ScrollToBottom(MainWindow window)
    {
        var scroll = window.GetVisualDescendants().OfType<SettingsDialog>().Single()
            .GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroll");
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        Pump();
    }

    private static void Save(MainWindow window, string name)
    {
        Pump();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(1440, 900), frame!.PixelSize);
        frame.Save(Path.Combine(Screenshots.Directory, name + ".png"), new PngBitmapEncoderOptions());
        window.Close();
    }

    [AvaloniaFact]
    public void The_models_table_shows_every_state()
    {
        var (window, editor) = Open(DesignScreen.Settings);
        ScrollToBottom(window);

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Download failed", texts);
        Assert.Contains("Not enough space", texts);
        Assert.Contains("Connection lost at 212 of 488 MB", texts);
        Assert.Contains("1.4 GB free on D:", texts);
        Assert.Contains(editor.Settings.NoSpaceText, texts);
        Save(window, "settings-models");
    }

    [AvaloniaFact]
    public void The_filler_word_box_shows_its_focus_ring()
    {
        var (window, editor) = Open(DesignScreen.Settings);
        var english = editor.Settings.FillerLanguages[0];
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => ReferenceEquals(t.DataContext, english));
        box.Focus();
        window.KeyTextInput("hm");

        Assert.Equal("hm", english.Draft);
        Save(window, "settings-fillers-focused");
    }

    [AvaloniaTheory]
    [InlineData("connected")]
    [InlineData("off")]
    [InlineData("waiting")]
    [InlineData("editing")]
    [InlineData("other")]
    public void The_status_card_shows_each_state(string state)
    {
        var (window, editor) = Open(DesignScreen.SettingsMcp);
        var claude = editor.Claude;
        switch (state)
        {
            case "off":
                editor.Settings.McpEnabled = false;
                break;
            case "waiting":
                claude.IsConnected = false;
                break;
            case "editing":
                claude.IsBusy = true;
                break;
            case "other":
                claude.IsConnected = false;
                claude.IsListening = false;
                claude.IsServedElsewhere = true;
                break;
        }
        Pump();

        var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "StatusCard");
        var title = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "StatusTitle");
        Assert.Equal(editor.Settings.McpStatusTitle, title.Text);
        var dot = card.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "Dot");
        Assert.Single(dot.Children, c => c.IsVisible);
        Save(window, "settings-mcp-" + state);
    }

    [AvaloniaFact]
    public void The_connect_claude_group_renders()
    {
        var (window, editor) = Open(DesignScreen.SettingsMcp);
        ScrollToBottom(window);

        var code = window.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(editor.Settings.ClaudeDesktopConfig, code);
        Assert.Contains(editor.Settings.ClaudeCodeCommand, code);
        Save(window, "settings-mcp-connect");
    }

    [AvaloniaFact]
    public void After_the_rename_the_connect_claude_group_says_to_add_it_again()
    {
        var (window, editor) = Open(DesignScreen.SettingsMcp);
        TextBlock Note(string name) => window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == name);
        Assert.False(Note("ClaudeCodeRenameNote").IsVisible);
        Assert.False(Note("ClaudeDesktopRenameNote").IsVisible);

        editor.Settings.ShowRenameNote = true;
        ScrollToBottom(window);

        Assert.True(Note("ClaudeCodeRenameNote").IsEffectivelyVisible);
        Assert.Contains("claude mcp remove --scope user ourcut", Note("ClaudeCodeRenameNote").Text, StringComparison.Ordinal);
        Assert.True(Note("ClaudeDesktopRenameNote").IsEffectivelyVisible);
        Save(window, "settings-mcp-renamed");
    }
}
