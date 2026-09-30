using Avalonia;
using Avalonia.Controls;
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
        Assert.Equal("Add to Claude Code", tour.SetupLabel);
        Assert.Equal("Open terminal", tour.OtherSetupLabel);
        // The demo screen shows what Add to Claude Code says; demo mode runs nothing.
        Assert.Equal(SettingsViewModel.AddedToClaudeCode, tour.SetupNote);

        tour.ClientOptions[1].PickCommand.Execute(null);
        Assert.False(tour.IsClaudeCode);
        Assert.Equal(editor.Settings.ClaudeDesktopConfig, tour.Command);
        Assert.Equal("Add to Claude Desktop", tour.SetupLabel);
        Assert.Equal("Open config file", tour.OtherSetupLabel);
        Assert.Contains("claude_desktop_config.json", tour.CommandHint, StringComparison.Ordinal);
        Assert.Equal("Your other servers stay, and the old file is kept as a .bak.", tour.SetupNote);

        tour.RunSetupCommand.Execute(null);
        Assert.Equal("Added to Claude Desktop. " + SettingsViewModel.RestartClaudeDesktop, tour.SetupNote);

        // Reopening starts clean.
        tour.Open();
        Assert.Null(editor.Settings.ClaudeDesktopResult);

        editor.Claude.IsConnected = true;
        Assert.True(tour.IsClaudeConnected);
        Assert.Equal("Claude connected", tour.ClaudeStatusText);
    }

    // A real (not demo) editor whose setup starts nothing and writes only in the test's folder.
    private (EditorViewModel Editor, FakeProcessRunner Runner, List<string> Opened, List<string> Copied) RealSetup(params string[] claude)
    {
        var editor = App.CreateEditor(null);
        var runner = new FakeProcessRunner();
        var opened = new List<string>();
        var copied = new List<string>();
        var settings = editor.Settings;
        // claude, if given, is found on the PATH.
        string path = string.Join(Path.PathSeparator, claude.Select(c => c[..c.LastIndexOfAny(['\\', '/'])]));
        settings.Setup = new ClaudeSetup(ClaudeSetup.CurrentPlatform, name => name == "PATH" ? path : null, claude.Contains, runner)
        {
            ScriptFolder = Path.Combine(_dir, "scripts"),
        };
        settings.ClaudeDesktopConfigFiles = () => [Path.Combine(_dir, "Claude", "claude_desktop_config.json")];
        settings.OpenFile = opened.Add;
        settings.CopyText = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };
        editor.Tour.Open();
        editor.Tour.Step = 3;
        return (editor, runner, opened, copied);
    }

    [AvaloniaFact]
    public void Without_claude_code_nothing_runs_and_the_step_links_to_its_install_page()
    {
        var (editor, runner, opened, copied) = RealSetup();
        var tour = editor.Tour;

        tour.RunSetupCommand.Execute(null);
        Assert.Contains("Claude Code isn’t installed", tour.SetupNote, StringComparison.Ordinal);
        Assert.True(tour.IsInstallLinkVisible);
        tour.RunOtherSetupCommand.Execute(null);
        Assert.Empty(runner.Ran);
        Assert.Empty(runner.Started);
        Assert.False(Directory.Exists(Path.Combine(_dir, "scripts")));

        editor.Settings.InstallClaudeCodeCommand.Execute(null);
        Assert.Equal([ClaudeSetup.InstallPage], opened);
        tour.CopyCommand.Execute(null);
        Assert.Equal([editor.Settings.ClaudeCodeCommand], copied);

        // Claude Desktop has no link.
        tour.IsClaudeCode = false;
        Assert.False(tour.IsInstallLinkVisible);
    }

    [AvaloniaFact]
    public void Add_to_claude_code_runs_it_hidden_and_open_terminal_runs_the_same_steps()
    {
        string claude = OperatingSystem.IsWindows() ? @"C:\Users\a\AppData\Roaming\npm\claude.cmd" : "/home/a/bin/claude";
        var (editor, runner, _, _) = RealSetup(claude);
        var tour = editor.Tour;
        editor.Settings.ShowRenameNote = true;

        tour.RunSetupCommand.Execute(null);
        Assert.Equal(SettingsViewModel.AddedToClaudeCode, tour.SetupNote);
        Assert.False(tour.IsInstallLinkVisible);
        // The old OurCut entry, an earlier HighlightCut, then the add.
        Assert.Equal(3, runner.Ran.Count);
        Assert.All(runner.Ran, r => Assert.True(r.CreateNoWindow));

        runner.RunResults.Enqueue(() => new ProcessOutput(0, ""));
        runner.RunResults.Enqueue(() => new ProcessOutput(0, ""));
        runner.RunResults.Enqueue(() => new ProcessOutput(1, "MCP server highlightcut already exists in user config"));
        tour.RunSetupCommand.Execute(null);
        Assert.Equal("Claude Code couldn’t add it: MCP server highlightcut already exists in user config", tour.SetupNote);

        tour.RunOtherSetupCommand.Execute(null);
        Assert.NotEmpty(runner.Started);
        string script = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(_dir, "scripts"))));
        Assert.Contains("mcp remove --scope user ourcut", script, StringComparison.Ordinal);
        Assert.Contains("mcp add --scope user highlightcut", script, StringComparison.Ordinal);
        Assert.StartsWith("Running in a new terminal.", tour.SetupNote, StringComparison.Ordinal);

        for (int i = 0; i < 4; i++)
            runner.StartResults.Enqueue(false);
        tour.RunOtherSetupCommand.Execute(null);
        Assert.Equal("Couldn’t open a terminal. The command is copied: paste it into one.", tour.SetupNote);
    }

    [AvaloniaFact]
    public void Add_to_claude_desktop_merges_the_file_or_leaves_it_and_copies_the_entry()
    {
        var (editor, _, opened, copied) = RealSetup();
        var tour = editor.Tour;
        tour.IsClaudeCode = false;
        string file = Path.Combine(_dir, "Claude", "claude_desktop_config.json");

        tour.RunOtherSetupCommand.Execute(null);
        Assert.Equal("There’s no claude_desktop_config.json yet. Add to Claude Desktop makes it.", tour.SetupNote);

        tour.RunSetupCommand.Execute(null);
        Assert.Equal("Added to Claude Desktop. " + SettingsViewModel.RestartClaudeDesktop, tour.SetupNote);
        Assert.Contains("\"highlightcut\"", File.ReadAllText(file), StringComparison.Ordinal);
        tour.RunSetupCommand.Execute(null);
        Assert.StartsWith("HighlightCut is already in Claude Desktop.", tour.SetupNote, StringComparison.Ordinal);

        File.WriteAllText(file, """{ "mcpServers": { "ourcut": { "command": "C:\\OurCut\\OurCut.exe" } } }""");
        tour.RunSetupCommand.Execute(null);
        Assert.StartsWith("Added to Claude Desktop, in place of the old OurCut entry.", tour.SetupNote, StringComparison.Ordinal);
        Assert.Empty(copied);

        File.WriteAllText(file, "{ broken");
        tour.RunSetupCommand.Execute(null);
        Assert.Equal("Nothing was changed: claude_desktop_config.json isn’t valid JSON. It’s open and the entry is copied: add it under mcpServers.",
            tour.SetupNote);
        Assert.Equal("{ broken", File.ReadAllText(file));
        Assert.Equal([editor.Settings.ClaudeDesktopConfig], copied);
        Assert.Equal([file], opened);

        tour.RunOtherSetupCommand.Execute(null);
        Assert.Equal([file, file], opened);
    }

    [AvaloniaFact]
    public void The_connect_claude_states_render()
    {
        var (editor, runner, _, _) = RealSetup();
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        var tour = editor.Tour;

        void Save(string name)
        {
            Pump();
            using var frame = window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(Screenshots.Directory, name + ".png"), new PngBitmapEncoderOptions());
        }

        tour.RunSetupCommand.Execute(null);
        Pump();
        var link = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "InstallLink");
        Assert.True(link.IsEffectivelyVisible);
        Save("welcome-claude-missing");

        editor.Settings.Setup = new ClaudeSetup(SetupPlatform.Linux, _ => null, _ => true, runner);
        runner.RunResults.Enqueue(() => new ProcessOutput(0, ""));
        runner.RunResults.Enqueue(() => new ProcessOutput(1, "Error: Invalid configuration: ~/.claude.json is not valid JSON. Fix or delete the file and try again."));
        tour.RunSetupCommand.Execute(null);
        Pump();
        Assert.False(link.IsEffectivelyVisible);
        Save("welcome-claude-failed");

        tour.IsClaudeCode = false;
        tour.RunSetupCommand.Execute(null);
        Save("welcome-claude-desktop-added");

        // The longest note still fits above the footer.
        File.WriteAllText(Path.Combine(_dir, "Claude", "claude_desktop_config.json"), "{ broken");
        tour.RunSetupCommand.Execute(null);
        Save("welcome-claude-desktop-failed");
        var note = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SetupNote");
        var stepText = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "StepText");
        Assert.True(note.TranslatePoint(new Point(0, note.Bounds.Height), window)!.Value.Y < stepText.TranslatePoint(default, window)!.Value.Y);
        window.Close();
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
