using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
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
using HighlightCut.Transcription;
using HighlightCut.Transcription.Models;

namespace HighlightCut.App.Tests;

/// <summary>
/// The welcome tour: when it opens, its steps, how it closes, its keys and focus, the Transcript step's model download,
/// the Connect Claude step's setup, and how it fits a small window.
/// </summary>
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

    private static void Press(Window window, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        window.KeyPress(key, mods, PhysicalKey.None, null);
        window.KeyRelease(key, mods, PhysicalKey.None, null);
        Pump();
    }

    private static (MainWindow Window, WelcomeTour View) Show(EditorViewModel editor, double width = 1440, double height = 900)
    {
        var window = new MainWindow { DataContext = editor, MinWidth = 0, MinHeight = 0, Width = width, Height = height };
        window.Show();
        Pump();
        Pump();
        return (window, window.GetVisualDescendants().OfType<WelcomeTour>().Single());
    }

    private static T Part<T>(WelcomeTour view, string name) where T : Control => view.FindControl<T>(name)!;

    private static IInputElement? Focused(Window window) => window.FocusManager?.GetFocusedElement();

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

    // ---- Transcript step: the model download ---------------------------------------------------------------------

    [AvaloniaFact]
    public void Transcript_step_offers_the_model_and_its_download_goes_on_after_the_tour()
    {
        var editor = App.CreateEditor(DesignScreen.WelcomeTranscript);
        var tour = editor.Tour;
        var offer = tour.ModelOffer;
        Assert.True(tour.IsTranscript);
        Assert.Same(editor.TranscriptPanel.ModelOffer, offer);
        Assert.False(offer.IsReady);
        Assert.True(offer.ShowDownloadButton);
        Assert.Equal("Download parakeet-tdt-0.6b-v3 (1.3 GB)", offer.DownloadButtonText);
        Assert.Equal("Multilingual, includes Ukrainian", offer.Note);

        offer.DownloadCommand.Execute(null);
        Assert.True(offer.IsDownloading);
        Assert.False(offer.ShowDownloadButton);
        Assert.Matches(new Regex(@"^\d\.\d of 1\.3 GB · 48 MB/s$"), offer.DownloadDetail);

        // Closed: the download goes on, and with no video nothing is queued for transcription.
        tour.CloseCommand.Execute(null);
        for (int i = 0; i < 400 && offer.IsDownloading; i++)
            editor.Settings.TickDownloads();

        Assert.True(offer.IsReady);
        Assert.Equal("parakeet-tdt-0.6b-v3", offer.ReadyModelId);
        Assert.False(editor.HasFile);
        Assert.False(editor.TranscriptPanel.TranscribeWhenInstalled);
    }

    [AvaloniaFact]
    public void Transcript_step_download_cancels_and_retries_after_a_failure()
    {
        var editor = App.CreateEditor(DesignScreen.WelcomeTranscript);
        var offer = editor.Tour.ModelOffer;

        offer.DownloadCommand.Execute(null);
        offer.CancelCommand.Execute(null);
        Assert.False(offer.IsDownloading);
        Assert.True(offer.ShowDownloadButton);

        offer.Model!.Error = "Could not download: 404";
        offer.Model.State = ModelState.Failed;
        Assert.Equal("Retry download (1.3 GB)", offer.DownloadButtonText);
        Assert.Equal("Could not download: 404", offer.Note);
        offer.DownloadCommand.Execute(null);
        Assert.True(offer.IsDownloading);
        Assert.Null(offer.Note);
    }

    [AvaloniaFact]
    public void Transcript_step_shows_the_installed_model_as_ready()
    {
        var editor = App.CreateEditor(DesignScreen.Welcome);
        editor.Tour.Step = 2;
        Assert.True(editor.Tour.ModelOffer.IsReady);

        var (window, view) = Show(editor);
        Assert.True(Part<StackPanel>(view, "ModelReady").IsEffectivelyVisible);
        Assert.False(Part<Button>(view, "DownloadButton").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_real_download_from_the_tour_transcribes_nothing()
    {
        var parakeet = ModelCatalog.Parakeet;
        byte[] archive = SettingsViewModelTests.Archive("sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", parakeet.Files);
        var preview = new TranscriptPanelTests.ScriptedPreview();
        var editor = new EditorViewModel { MediaOpener = new TranscriptPanelTests.ScriptedOpener(preview) };
        editor.Settings.ModelsFolder = Directory.CreateDirectory(Path.Combine(_dir, "models")).FullName;
        editor.Settings.Installer = new ModelInstaller(new HttpClient(SettingsViewModelTests.FakeServer.Serving(archive)));
        editor.Settings.TranscribeOnOpen = false;
        var offer = editor.Tour.ModelOffer;
        editor.Tour.Open();
        Assert.Equal("Download parakeet-tdt-0.6b-v3 (487 MB)", offer.DownloadButtonText);

        offer.DownloadCommand.Execute(null);
        for (int i = 0; i < 250 && !offer.IsReady; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(offer.IsReady);
        Assert.Equal(parakeet.Id, offer.ReadyModelId);

        // A video opened afterwards is not transcribed because of the tour's download.
        await editor.OpenMediaAsync(Path.Combine(_dir, "talk.mp4"));
        Assert.Equal(0, preview.Started);
    }

    [AvaloniaFact]
    public void Transcript_step_renders_the_offer_and_the_progress()
    {
        foreach (var screen in (DesignScreen[])[DesignScreen.WelcomeTranscript, DesignScreen.WelcomeDownloading])
        {
            var editor = App.CreateEditor(screen);
            var (window, view) = Show(editor);
            bool downloading = screen == DesignScreen.WelcomeDownloading;
            Assert.Equal(!downloading, Part<Button>(view, "DownloadButton").IsEffectivelyVisible);
            Assert.Equal(downloading, Part<ModelDownloadProgress>(view, "ModelProgress").IsEffectivelyVisible);
            Assert.Equal(!downloading, Part<TextBlock>(view, "ModelNote").IsEffectivelyVisible);
            window.Close();
        }
    }

    // ---- Keys and focus -----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Enter_and_the_arrows_move_through_the_steps()
    {
        var tour = App.CreateEditor(null).Tour;
        tour.Open();

        Assert.True(tour.HandleKey(Key.Right));
        Assert.True(tour.IsShortcuts);
        Assert.True(tour.HandleKey(Key.Left));
        Assert.True(tour.HandleKey(Key.Left));
        Assert.True(tour.IsOpenAndCut);
        Assert.True(tour.HandleKey(Key.Enter));
        Assert.True(tour.IsShortcuts);
        tour.Step = 3;
        Assert.True(tour.HandleKey(Key.Right));
        Assert.True(tour.IsConnectClaude);
        Assert.True(tour.IsOpen);
        Assert.False(tour.HandleKey(Key.Up));
    }

    [AvaloniaFact]
    public void Keys_in_the_window_continue_and_step_but_leave_a_drop_down_alone()
    {
        var editor = App.CreateEditor(DesignScreen.Welcome);
        var (window, view) = Show(editor);
        var next = Part<Button>(view, "NextButton");
        Assert.Same(next, Focused(window));

        Press(window, Key.Right);
        Assert.True(editor.Tour.IsShortcuts);
        Press(window, Key.Left);
        Assert.True(editor.Tour.IsOpenAndCut);
        Press(window, Key.Enter);
        Press(window, Key.Enter);
        Assert.True(editor.Tour.IsTranscript);

        // The language drop-down keeps its arrows.
        var language = Part<ComboBox>(view, "LanguageBox");
        language.Focus();
        Press(window, Key.Right);
        Press(window, Key.Left);
        Assert.True(editor.Tour.IsTranscript);

        // Back, focused, goes back with Enter; on the first step it is hidden and Continue takes focus.
        var back = Part<Button>(view, "BackButton");
        editor.Tour.Step = 1;
        Pump();
        back.Focus();
        Press(window, Key.Enter);
        Assert.True(editor.Tour.IsOpenAndCut);
        Pump();
        Assert.Same(next, Focused(window));

        // Enter on the last step opens a video (the demo's sample).
        editor.Tour.Step = 3;
        Pump();
        Press(window, Key.Enter);
        Assert.False(editor.Tour.IsOpen);
        Assert.True(editor.HasFile);
        window.Close();
    }

    [AvaloniaFact]
    public void Tab_stays_in_the_dialog_and_focus_comes_back_after_it()
    {
        var editor = App.CreateEditor(DesignScreen.Empty);
        var (window, view) = Show(editor);
        // The editor's own buttons take no focus; one that does stands in for whatever had it.
        var before = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && !view.IsVisualAncestorOf(b));
        before.Focusable = true;
        before.Focus();
        Assert.Same(before, Focused(window));

        editor.Tour.Open();
        Pump();
        var dialog = Part<Border>(view, "Dialog");
        Assert.Same(Part<Button>(view, "NextButton"), Focused(window));
        for (int step = 0; step < editor.Tour.Steps.Count; step++)
        {
            editor.Tour.Step = step;
            Pump();
            for (int i = 0; i < 24; i++)
            {
                Press(window, Key.Tab, i % 3 == 2 ? RawInputModifiers.Shift : RawInputModifiers.None);
                Assert.True(Focused(window) is Visual v && dialog.IsVisualAncestorOf(v), $"Tab left the dialog on step {step + 1}");
            }
        }

        Press(window, Key.Escape);
        Assert.False(editor.Tour.IsOpen);
        Assert.Same(before, Focused(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Buttons_and_steps_have_names_and_the_step_is_announced()
    {
        var editor = App.CreateEditor(DesignScreen.Welcome);
        var (window, view) = Show(editor);

        Assert.Equal("Continue", AutomationProperties.GetName(Part<Button>(view, "NextButton")));
        Assert.Equal("Skip tour", AutomationProperties.GetName(Part<Button>(view, "SkipButton")));
        Assert.Equal("Back", AutomationProperties.GetName(Part<Button>(view, "BackButton")));
        Assert.Equal("Tour steps", AutomationProperties.GetName(Part<ItemsControl>(view, "StepItems")));
        var steps = Part<ItemsControl>(view, "StepItems").GetVisualDescendants().OfType<Button>().Select(AutomationProperties.GetName);
        Assert.Equal(["Step 1 of 4, Open & cut, current", "Step 2 of 4, Shortcuts", "Step 3 of 4, Transcript", "Step 4 of 4, Connect Claude"],
            steps);

        var status = Part<TextBlock>(view, "StepText");
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
        editor.Tour.Step = 3;
        Pump();
        Assert.Equal("Step 4 of 4: Connect Claude", AutomationProperties.GetName(status));
        Assert.Equal("Open a video", AutomationProperties.GetName(Part<Button>(view, "NextButton")));
        Assert.Equal("Step 1 of 4, Open & cut, done", editor.Tour.Steps[0].AccessibleName);
        window.Close();
    }

    // ---- Open & cut: the timeline chips ---------------------------------------------------------------------------

    [AvaloniaFact]
    public void Open_and_cut_explains_the_timeline_chips()
    {
        var editor = App.CreateEditor(DesignScreen.Welcome);
        var (window, view) = Show(editor);

        var chips = Part<WrapPanel>(view, "Chips").Children.OfType<Border>().ToList();
        Assert.Equal(["Frames", "Keyframes", "Waveform", "Silence", "Scenes", "Transcript", "Snap"],
            chips.Select(c => c.GetVisualDescendants().OfType<TextBlock>().Last().Text));
        // As on a first start: only Snap is on.
        Assert.Equal(["Snap"], chips.Where(c => c.Classes.Contains("on")).Select(c => c.GetVisualDescendants().OfType<TextBlock>().Last().Text));
        var defaults = new TimelineSettings();
        Assert.Equal((false, false, false, false, false, true),
            (defaults.Frames, defaults.Keyframes, defaults.Waveform, defaults.Silences, defaults.Scenes, defaults.Snap));
        Assert.False(AppSettings.Default.Transcription.TranscribeOnOpen);

        string text = Part<TextBlock>(view, "ChipsText").Text!;
        Assert.Contains("runs only while the chip is on", text, StringComparison.Ordinal);
        Assert.Contains("lossless export reads the keyframes", text, StringComparison.Ordinal);
        // Fits the step without scrolling at the design size.
        var scroll = Part<ScrollViewer>(view, "StepScroll");
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 0.5, $"{scroll.Extent.Height} > {scroll.Viewport.Height}");
        window.Close();
    }

    // ---- Small windows ------------------------------------------------------------------------------------------

    public static TheoryData<int, int> WindowSizes { get; } = new() { { 1100, 700 }, { 900, 580 }, { 760, 480 }, { 600, 400 } };

    [AvaloniaTheory]
    [MemberData(nameof(WindowSizes))]
    public void Fits_a_small_window_with_the_buttons_in_view(int width, int height)
    {
        var editor = App.CreateEditor(DesignScreen.WelcomeTranscript);
        var (window, view) = Show(editor, width, height);
        var dialog = Part<Border>(view, "Dialog");
        var bounds = dialog.Bounds;
        var fits = new Rect(0, 0, width, height).Deflate(16);

        Assert.Equal(Math.Min(820, width - 40), bounds.Width, 0.5);
        Assert.Equal(Math.Min(520, height - 40), bounds.Height, 0.5);
        for (int step = 0; step < editor.Tour.Steps.Count; step++)
        {
            editor.Tour.Step = step;
            Pump();
            foreach (string name in (string[])["NextButton", "SkipButton", "StepText"])
            {
                var part = Part<Control>(view, name);
                var box = new Rect(part.TranslatePoint(default, window)!.Value, part.Bounds.Size);
                Assert.True(fits.Contains(box), $"{name} at {box} is outside {fits} on step {step + 1}");
            }
            if (step == 2 || (step == 0 && width == 1100))
            {
                using var frame = window.CaptureRenderedFrame();
                frame!.Save(Path.Combine(Screenshots.Directory, $"welcome-{width}x{height}-step-{step + 1}.png"), new PngBitmapEncoderOptions());
            }
        }
        window.Close();
    }

    [AvaloniaFact]
    public void The_reopen_note_sits_above_the_player_controls()
    {
        var editor = App.CreateEditor(DesignScreen.Welcome);
        var (window, view) = Show(editor);
        editor.Tour.CloseCommand.Execute(null);
        Pump();
        Pump();

        var note = Part<Border>(view, "ReopenNote");
        var transport = window.GetVisualDescendants().OfType<PlayerPanel>().Single().FindControl<Control>("Transport")!;
        double noteBottom = note.TranslatePoint(new Point(0, note.Bounds.Height), window)!.Value.Y;
        double transportTop = transport.TranslatePoint(default, window)!.Value.Y;
        Assert.Equal(transportTop - 28, noteBottom, 0.5);
        window.Close();
    }
}
