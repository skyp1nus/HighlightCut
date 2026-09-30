using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.Demo;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;

namespace HighlightCut.App.Tests;

/// <summary>The welcome tour: when it opens, its steps, how it closes, and the Connect Claude step's setup.</summary>
public sealed class WelcomeTourTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-welcome").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Opens_at_start_only_for_an_empty_first_launch()
    {
        var editor = App.CreateEditor(null);
        Assert.True(editor.Tour.ShouldOpenAtStart(null));
        Assert.False(editor.Tour.ShouldOpenAtStart("talk.mp4"));

        // A last project to reopen: the editor won't be empty.
        editor.RecentFiles.Add(new RecentFileViewModel("talk.mp4", "talk.mp4", "", ""));
        Assert.False(editor.Tour.ShouldOpenAtStart(null));
        editor.Settings.Startup = StartupAction.StartEmpty;
        Assert.True(editor.Tour.ShouldOpenAtStart(null));

        editor.Settings.Load(AppSettings.Default with { WelcomeTourSeen = true });
        Assert.False(editor.Tour.ShouldOpenAtStart(null));
        Assert.False(App.CreateEditor(DesignScreen.Empty).Tour.ShouldOpenAtStart(null));
    }

    [AvaloniaFact]
    public void Steps_go_forward_back_and_by_the_list()
    {
        var tour = App.CreateEditor(null).Tour;
        tour.Open();

        Assert.Equal(["Open & cut", "Shortcuts", "Transcript", "Connect Claude"], tour.Steps.Select(s => s.Title));
        Assert.True(tour.IsOpenAndCut);
        Assert.False(tour.HasBack);
        Assert.Equal("1 of 4", tour.StepText);
        Assert.Equal("Continue", tour.NextLabel);
        Assert.Equal(["1", "2", "3", "4"], tour.Steps.Select(s => s.Number));

        tour.NextCommand.Execute(null);
        tour.NextCommand.Execute(null);
        Assert.True(tour.IsTranscript);
        Assert.Equal(["✓", "✓", "3", "4"], tour.Steps.Select(s => s.Number));
        Assert.True(tour.Steps[2].IsCurrent);

        tour.BackCommand.Execute(null);
        Assert.True(tour.IsShortcuts);

        tour.Steps[3].GoCommand.Execute(null);
        Assert.True(tour.IsConnectClaude);
        Assert.True(tour.IsLast);
        Assert.Equal("Open a video", tour.NextLabel);
        Assert.Equal("4 of 4", tour.StepText);
    }

    [AvaloniaFact]
    public void Skipping_saves_that_it_was_seen_and_leaves_a_note_with_replay()
    {
        var editor = App.CreateEditor(null);
        string file = Path.Combine(_dir, "settings.json");
        editor.Settings.Store = new AppSettingsStore(file);
        var tour = editor.Tour;
        tour.Open();
        tour.NextCommand.Execute(null);

        tour.CloseCommand.Execute(null);

        Assert.False(tour.IsOpen);
        Assert.True(tour.IsNoteVisible);
        Assert.True(editor.Settings.WelcomeTourSeen);
        Assert.True(new AppSettingsStore(file).Load().WelcomeTourSeen);

        tour.OpenCommand.Execute(null);
        Assert.True(tour.IsOpen);
        Assert.False(tour.IsNoteVisible);
        Assert.True(tour.IsOpenAndCut);
    }

    [AvaloniaFact]
    public void Esc_skips_and_other_editor_keys_do_nothing_under_the_tour()
    {
        var editor = App.CreateEditor(null);
        editor.Tour.Open();

        Assert.False(Shortcuts.Handle(editor, Key.E, KeyModifiers.Control));
        Assert.False(editor.Export.IsDialogOpen);
        Assert.True(Shortcuts.Handle(editor, Key.Escape, KeyModifiers.None));
        Assert.False(editor.Tour.IsOpen);
    }

    [AvaloniaFact]
    public void Open_a_video_and_its_key_finish_the_tour_and_open_one()
    {
        // In the demo's empty screen, opening a file loads the sample.
        var editor = App.CreateEditor(DesignScreen.Welcome);
        Assert.True(editor.Tour.IsOpen);

        Assert.True(Shortcuts.Handle(editor, Key.O, KeyModifiers.Control));

        Assert.False(editor.Tour.IsOpen);
        Assert.True(editor.HasFile);

        editor = App.CreateEditor(DesignScreen.WelcomeClaude);
        editor.Tour.NextCommand.Execute(null);
        Assert.False(editor.Tour.IsOpen);
        Assert.True(editor.HasFile);
    }

    [AvaloniaFact]
    public void Opening_a_file_otherwise_closes_the_tour()
    {
        var editor = App.CreateEditor(DesignScreen.Welcome);
        DemoScenario.OpenSample(editor);
        Assert.False(editor.Tour.IsOpen);
    }

    [AvaloniaFact]
    public void Shortcuts_step_shows_the_current_keys()
    {
        var editor = App.CreateEditor(null);
        var keys = editor.Tour.Keys;
        Assert.Equal(["Space", "← →", "I", "O", "S", "E"], keys.Select(k => k.Key));

        editor.Settings.KeyMap.Assign(ShortcutAction.Split, new KeyCombo(Key.K, KeyModifiers.None));
        Assert.Equal("K", editor.Tour.Keys[4].Key);
    }

    [AvaloniaFact]
    public void Transcript_step_edits_the_transcription_settings()
    {
        var editor = App.CreateEditor(null);
        var tour = editor.Tour;
        tour.Settings.Language = "Ukrainian";
        tour.Settings.TranscribeOnOpen = true;

        Assert.Equal("Ukrainian", editor.Settings.Current.Transcription.Language);
        Assert.True(editor.Settings.TranscribeOnOpen);
    }

    [AvaloniaFact]
    public void Connect_claude_switches_between_the_command_and_the_config_entry()
    {
        var editor = App.CreateEditor(DesignScreen.WelcomeClaude);
        var tour = editor.Tour;

        Assert.False(tour.IsClaudeConnected);
        Assert.Equal("Waiting for Claude", tour.ClaudeStatusText);
        Assert.Equal(editor.Settings.ClaudeCodeCommand, tour.Command);
        Assert.Equal("Open terminal", tour.SetupLabel);
        Assert.Equal("Opens a terminal and runs the command.", tour.SetupNote);

        tour.ClientOptions[1].PickCommand.Execute(null);
        Assert.False(tour.IsClaudeCode);
        Assert.Equal(editor.Settings.ClaudeDesktopConfig, tour.Command);
        Assert.Equal("Open config file", tour.SetupLabel);
        Assert.Contains("claude_desktop_config.json", tour.CommandHint, StringComparison.Ordinal);

        tour.RunSetupCommand.Execute(null);
        Assert.Equal("Created with HighlightCut in it. Restart Claude Desktop.", tour.SetupNote);

        editor.Claude.IsConnected = true;
        Assert.True(tour.IsClaudeConnected);
        Assert.Equal("Claude connected", tour.ClaudeStatusText);
    }

    [Fact]
    public void Desktop_config_is_created_with_highlightcut_or_checked_for_it()
    {
        string file = Path.Combine(_dir, "Claude", "claude_desktop_config.json");
        const string Entry = "{\n  \"mcpServers\": {\n    \"highlightcut\": {\n      \"command\": \"hc\",\n      \"args\": [\"mcp\"]\n    }\n  }\n}";

        Assert.Equal(DesktopConfigState.Created, ClaudeSetup.PrepareDesktopConfig(file, Entry));
        Assert.True(ClaudeSetup.HasHighlightCut(File.ReadAllText(file)));
        Assert.Equal(DesktopConfigState.AlreadyAdded, ClaudeSetup.PrepareDesktopConfig(file, Entry));

        File.WriteAllText(file, "{ \"mcpServers\": { \"other\": { \"command\": \"x\" } } }");
        Assert.Equal(DesktopConfigState.NeedsEntry, ClaudeSetup.PrepareDesktopConfig(file, Entry));
        Assert.False(ClaudeSetup.HasHighlightCut("not json"));
        Assert.False(ClaudeSetup.HasHighlightCut("[]"));
    }

    [AvaloniaFact]
    public void Each_step_renders_in_the_window()
    {
        var editor = App.CreateEditor(DesignScreen.Welcome);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        var view = window.GetVisualDescendants().OfType<WelcomeTour>().Single();

        for (int step = 0; step < editor.Tour.Steps.Count; step++)
        {
            editor.Tour.Step = step;
            Pump();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(Screenshots.Directory, $"welcome-step-{step + 1}.png"), new PngBitmapEncoderOptions());
        }
        Assert.True(view.IsVisible);

        editor.Tour.CloseCommand.Execute(null);
        Pump();
        using var closed = window.CaptureRenderedFrame();
        closed!.Save(Path.Combine(Screenshots.Directory, "welcome-closed.png"), new PngBitmapEncoderOptions());
        window.Close();
    }
}
