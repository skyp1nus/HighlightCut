using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using HighlightCut.App.Demo;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;
using HighlightCut.Core.Transcripts;
using HighlightCut.Transcription;
using HighlightCut.Transcription.Models;
using static HighlightCut.App.Tests.SettingsViewModelTests;

namespace HighlightCut.App.Tests;

/// <summary>Settings → Transcription (transcribe on open, filler words, failed and too-big models) and Settings → MCP server.</summary>
public sealed class SettingsTranscriptionMcpTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-settings-tm").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task PumpUntil(Func<bool> done)
    {
        for (int i = 0; i < 250 && !done(); i++)
        {
            await Task.Delay(20, Ct);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(done());
    }

    private static FillerLanguageViewModel Language(SettingsViewModel settings, string code) =>
        settings.FillerLanguages.Single(l => l.Code == code);

    private static IEnumerable<string> Chips(FillerLanguageViewModel language) => language.Words.Select(w => w.Word);

    private string EmptyFolder(string name) => Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;

    // ---- Transcription -------------------------------------------------------------------

    [AvaloniaFact]
    public void Transcribe_on_open_and_filler_words_are_saved_and_read_back()
    {
        var store = new AppSettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = App.CreateEditor(null).Settings;
        settings.Store = store;
        Assert.False(settings.TranscribeOnOpen);
        Assert.Equal(["English", "Ukrainian"], settings.FillerLanguages.Select(l => l.Label));
        Assert.Equal(FillerWords.English, Chips(Language(settings, "en")));

        settings.TranscribeOnOpen = true;
        var english = Language(settings, "en");
        english.Draft = "  Hmm ";
        english.AddDraftCommand.Execute(null);
        Language(settings, "uk").Words.Single(w => w.Word == "ну").RemoveCommand.Execute(null);

        Assert.Equal("", english.Draft);
        Assert.Equal("hmm", Chips(english).Last());
        Assert.Equal("hmm", english.Entries[^2] is FillerWordViewModel { Word: var last } ? last : null);
        Assert.Same(english, english.Entries[^1]);
        var saved = store.Load().Transcription;
        Assert.True(saved.TranscribeOnOpen);
        Assert.NotNull(saved.FillerWords);
        Assert.Equal("hmm", saved.FillerWords["en"][^1]);
        Assert.DoesNotContain("ну", saved.FillerWords["uk"]);

        var reloaded = App.CreateEditor(null).Settings;
        reloaded.Load(store.Load());
        Assert.True(reloaded.TranscribeOnOpen);
        Assert.Equal(Chips(english), Chips(Language(reloaded, "en")));
        Assert.Equal(["е-е", "типу", "короче"], Chips(Language(reloaded, "uk")));

        english.Words.Single(w => w.Word == "hmm").RemoveCommand.Execute(null);
        Language(settings, "uk").Draft = "ну";
        Language(settings, "uk").AddDraftCommand.Execute(null);
        Assert.Equal(["е-е", "типу", "короче", "ну"], Chips(Language(settings, "uk")));
        settings.SetFillerWords("uk", FillerWords.Ukrainian);
        Assert.Null(store.Load().Transcription.FillerWords);
    }

    [AvaloniaFact]
    public void Adding_a_filler_word_skips_duplicates_and_backspace_removes_the_last()
    {
        var settings = App.CreateEditor(null).Settings;
        int changes = 0;
        settings.FillerWordsChanged += (_, _) => changes++;
        var english = Language(settings, "en");

        english.Draft = "UM";
        english.AddDraftCommand.Execute(null);
        Assert.Equal(FillerWords.English, Chips(english));
        Assert.Equal("", english.Draft);
        Assert.Equal(0, changes);

        english.Draft = "x";
        Assert.False(english.RemoveLastCommand.CanExecute(null));
        english.Draft = "";
        Assert.True(english.RemoveLastCommand.CanExecute(null));
        english.RemoveLastCommand.Execute(null);
        Assert.Equal(["um", "uh", "er", "like"], Chips(english));
        Assert.Equal(1, changes);
        Assert.Equal(["um", "uh", "er", "like", "е-е", "ну", "типу", "короче"], FillerWords.All(settings.FillerWords));

        foreach (string _ in FillerWords.English.SkipLast(1))
            english.RemoveLastCommand.Execute(null);
        Assert.Empty(english.Words);
        Assert.False(english.RemoveLastCommand.CanExecute(null));
        Assert.Single(english.Entries);
    }

    [AvaloniaFact]
    public void Enter_in_the_add_box_adds_the_word_and_backspace_removes_the_last()
    {
        var editor = App.CreateEditor(DesignScreen.Settings);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var english = Language(editor.Settings, "en");
        TextBox Box() => window.GetVisualDescendants().OfType<TextBox>().Single(t => ReferenceEquals(t.DataContext, english));

        Box().Focus();
        window.KeyTextInput("hmm");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("hmm", english.Draft);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([.. FillerWords.English, "hmm"], Chips(english));
        Assert.Equal("", english.Draft);
        Assert.True(Box().IsFocused);

        window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["um", "uh", "er", "like"], Chips(english));
        Assert.False(editor.IsPlaying);
        window.Close();
    }

    [AvaloniaFact]
    public void A_model_that_does_not_fit_cannot_be_downloaded()
    {
        var settings = App.CreateEditor(null).Settings;
        settings.Installer = new ModelInstaller(new HttpClient(FakeServer.Serving([])));
        settings.ProbeDisk = _ => new DiskSpace(100_000_000, "D:");
        settings.ModelsFolder = EmptyFolder("models");

        var small = settings.Models.Single(m => m.Id == "whisper-small");
        Assert.True(small.IsNoSpace);
        Assert.Equal("Needs 639 MB · 100 MB free on D:", small.Note);
        small.DownloadCommand.Execute(null);
        Assert.True(small.IsNoSpace);
        Assert.True(settings.HasNoSpaceModels);
        Assert.StartsWith("4 models need more than the 100 MB free on D:", settings.NoSpaceText, StringComparison.Ordinal);
        Assert.Equal("100 MB free on D:", settings.DiskFreeText);

        settings.ProbeDisk = _ => new DiskSpace(10_000_000_000, null);
        settings.OpenCommand.Execute(null);
        Assert.All(settings.Models, m => Assert.True(m.IsNotInstalled));
        Assert.False(settings.HasNoSpaceModels);
        Assert.Equal("", settings.NoSpaceText);
        Assert.Equal("10 GB free", settings.DiskFreeText);
        Assert.Equal("Multilingual, includes Ukrainian", settings.Models[0].Note);
        Assert.Null(small.Note);
    }

    [Theory]
    [InlineData(212_000_000_000, "212 GB")]
    [InlineData(10_000_000_000, "10 GB")]
    [InlineData(1_400_000_000, "1.4 GB")]
    [InlineData(850_400_000, "850 MB")]
    [InlineData(20_000, "1 MB")]
    public void Free_space_reads_like_the_design(long bytes, string text) => Assert.Equal(text, SettingsViewModel.FormatFree(bytes));

    [AvaloniaFact]
    public async Task A_failed_download_can_be_retried()
    {
        var parakeet = ModelCatalog.Parakeet;
        byte[] archive = Archive("sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", parakeet.Files);
        int requests = 0;
        var editor = App.CreateEditor(null);
        var settings = editor.Settings;
        settings.ModelsFolder = EmptyFolder("models");
        settings.Installer = new ModelInstaller(new HttpClient(new FakeServer(_ => Task.FromResult(requests++ == 0
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }))));
        var model = settings.Models.Single(m => m.Id == parakeet.Id);

        model.DownloadCommand.Execute(null);
        await PumpUntil(() => model.IsFailed);
        Assert.Contains("404", model.Note, StringComparison.Ordinal);
        Assert.StartsWith("Could not download", editor.StatusMessage, StringComparison.Ordinal);

        settings.OpenCommand.Execute(null);
        Assert.True(model.IsFailed);
        model.RetryCommand.Execute(null);
        Assert.True(model.IsDownloading);
        Assert.Null(model.Error);
        await PumpUntil(() => model.IsInstalled);
        Assert.Equal("Best available — parakeet-tdt-0.6b-v3", settings.ModelOptions[0].Label);
    }

    /// <summary>Opens every path as a small file that counts its transcription requests.</summary>
    private sealed class CountingOpener : IMediaOpener
    {
        public int Transcriptions { get; private set; }

        public TranscriptionSetup? LastSetup { get; private set; }

        private sealed class Preview(CountingOpener owner) : IMediaPreview
        {
            public double Duration => 10;
            public double FrameRate => 25;
            public IReadOnlyList<double> Keyframes => [0];
            public int AudioStreamCount => 1;
            public bool IsPlaceholder => false;
            public bool IsPlayable => false;
            public string? Activity => null;
            public string? AnalysisError => null;
            public TranscriptState TranscriptState { get; private set; }

            public event EventHandler? Changed
            {
                add { }
                remove { }
            }

            public void StartTranscription(TranscriptionSetup setup)
            {
                owner.Transcriptions++;
                owner.LastSetup = setup;
                TranscriptState = TranscriptState.Waiting;
            }

            public double AudioPeak(int stream, double startTime, double endTime) => 0;

            public void DrawFrame(DrawingContext context, Rect rect, double time, FrameLook look, int variant)
            {
            }
        }

        public Task<OpenedMedia> OpenAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenedMedia(new HighlightCut.Core.Model.SourceMedia(path, 10, 25, [new(1, "Mic")]), new Preview(this), "talk.mp4"));
    }

    [AvaloniaFact]
    public async Task Turning_off_transcribe_on_open_leaves_a_new_video_untranscribed()
    {
        string models = EmptyFolder("models");
        string parakeet = Directory.CreateDirectory(Path.Combine(models, ModelCatalog.Parakeet.Id)).FullName;
        foreach (string file in ModelCatalog.Parakeet.Files)
            File.WriteAllText(Path.Combine(parakeet, file), "");
        var opener = new CountingOpener();
        var editor = App.CreateEditor(null, opener);
        editor.Settings.ModelsFolder = models;
        editor.Settings.TranscribeOnOpen = false;

        await editor.OpenMediaAsync("/videos/talk.mp4");
        Assert.Equal(0, opener.Transcriptions);
        editor.Settings.Language = "English";
        Assert.Equal(0, opener.Transcriptions);

        Assert.Null(editor.StartTranscription());
        Assert.Equal(1, opener.Transcriptions);
        // A transcript someone asked for is redone when the language changes.
        editor.Settings.Language = "Ukrainian";
        Assert.Equal(2, opener.Transcriptions);

        // The setting and the Transcript chip are one choice: on, the open video is transcribed too.
        Assert.False(editor.ShowTranscriptLane);
        editor.Settings.TranscribeOnOpen = true;
        Assert.True(editor.ShowTranscriptLane);
        Assert.Equal(3, opener.Transcriptions);
        await editor.OpenMediaAsync("/videos/other.mp4");
        Assert.Equal(4, opener.Transcriptions);
    }

    [AvaloniaFact]
    public async Task The_chosen_device_reaches_the_recognizer_and_the_note_says_what_it_runs_on()
    {
        string models = EmptyFolder("models");
        string parakeet = Directory.CreateDirectory(Path.Combine(models, ModelCatalog.Parakeet.Id)).FullName;
        foreach (string file in ModelCatalog.Parakeet.Files)
            File.WriteAllText(Path.Combine(parakeet, file), "");
        var opener = new CountingOpener();
        var editor = App.CreateEditor(null, opener);
        var settings = editor.Settings;
        settings.ModelsFolder = models;
        await editor.OpenMediaAsync("/videos/talk.mp4");

        settings.Device = "CPU";
        Assert.Null(editor.StartTranscription());
        Assert.Equal(TranscriptionDevice.Cpu, opener.LastSetup?.Device);

        string threads = RecognizerPlan.Cpu(Environment.ProcessorCount).Description;
        settings.GpuProvider = null;
        Assert.Equal(threads, settings.DeviceNote);
        settings.Device = "Auto";
        Assert.Equal(threads + " · no GPU runtime is installed", settings.DeviceNote);
        settings.Device = "GPU";
        Assert.Equal("No GPU runtime is installed · choose Auto or CPU", settings.DeviceNote);
        settings.GpuProvider = "cuda";
        Assert.Equal("GPU (CUDA)", settings.DeviceNote);
        settings.Device = "Auto";
        Assert.Equal("GPU (CUDA) · CPU if it fails", settings.DeviceNote);
    }

    [AvaloniaFact]
    public async Task With_DirectML_the_note_names_the_GPU_and_what_its_check_found()
    {
        string models = EmptyFolder("models");
        string parakeet = Directory.CreateDirectory(Path.Combine(models, ModelCatalog.Parakeet.Id)).FullName;
        foreach (string file in ModelCatalog.Parakeet.Files)
            File.WriteAllText(Path.Combine(parakeet, file), "");
        var editor = App.CreateEditor(null, new CountingOpener());
        var settings = editor.Settings;
        settings.ModelsFolder = models;
        await editor.OpenMediaAsync("/videos/talk.mp4");
        string threads = RecognizerPlan.Cpu(Environment.ProcessorCount).Description;
        GpuCheck? check = null;
        settings.GpuProvider = "directml";
        settings.GpuName = "AMD Radeon RX 7800 XT";
        settings.GpuCheckFor = model => model == ModelCatalog.Parakeet ? check : null;

        settings.Device = "Auto";
        Assert.Equal("GPU (DirectML) · AMD Radeon RX 7800 XT · checked when transcription starts", settings.DeviceNote);
        check = new GpuCheck(true, 3.24);
        Assert.Equal("GPU (DirectML) · AMD Radeon RX 7800 XT · 3.2× faster than the CPU", settings.DeviceNote);
        check = new GpuCheck(true, 0.6);
        Assert.Equal(threads + " · faster than DirectML on AMD Radeon RX 7800 XT", settings.DeviceNote);
        check = new GpuCheck(false, Problem: "it did not start (the driver is too old)");
        Assert.Equal(threads + " · DirectML: it did not start (the driver is too old)", settings.DeviceNote);

        settings.Device = "GPU";
        Assert.Equal("DirectML: it did not start (the driver is too old) · choose Auto or CPU", settings.DeviceNote);
        check = new GpuCheck(true, 0.6);
        Assert.Equal("GPU (DirectML) · AMD Radeon RX 7800 XT · slower than the CPU", settings.DeviceNote);
        settings.Device = "CPU";
        Assert.Equal(threads, settings.DeviceNote);

        settings.Device = "Auto";
        settings.GpuName = null;
        check = GpuCheck.NoGpu;
        Assert.Equal(threads + " · DirectML: there is no GPU it can use", settings.DeviceNote);
    }

    [AvaloniaFact]
    public void Leaving_the_demo_brings_back_the_catalog_models_and_the_real_disk()
    {
        var editor = App.CreateEditor(DesignScreen.Settings);
        var settings = editor.Settings;
        Assert.Equal(5, settings.Models.Count);
        Assert.Equal("claude mcp add --scope user highlightcut -- \"C:\\Program Files\\HighlightCut\\HighlightCut.exe\" mcp", settings.ClaudeCodeCommand);
        settings.Close();

        editor.LeaveDemo();
        settings.ProbeDisk = _ => new DiskSpace(50_000_000_000, null);
        settings.ModelsFolder = EmptyFolder("models");
        settings.Open();

        Assert.Equal(ModelCatalog.All.Select(m => m.Id), settings.Models.Select(m => m.Id));
        Assert.Equal(ModelCatalog.All.Select(m => m.SizeText), settings.Models.Select(m => m.Size));
        Assert.Equal("50 GB free", settings.DiskFreeText);
        Assert.Equal(SettingsViewModel.ClaudeDesktopConfigPath, settings.McpConfigPathText);
        Assert.DoesNotContain("C:\\Program Files\\HighlightCut", settings.ClaudeCodeCommand, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Leaving_the_demo_drops_its_MCP_connection()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        Assert.Equal(McpStatus.Connected, editor.Claude.Status);

        DemoScenario.OpenSample(editor);

        Assert.Equal(McpStatus.Off, editor.Claude.Status);
        Assert.Null(editor.Claude.ClientName);
    }

    // ---- Settings file ---------------------------------------------------------------------

    [AvaloniaFact]
    public void Old_settings_files_read_the_new_defaults()
    {
        string file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, """{ "transcription": { "engine": "Whisper" } }""");
        var settings = App.CreateEditor(null).Settings;
        settings.Load(new AppSettingsStore(file).Load());

        Assert.False(settings.TranscribeOnOpen);
        Assert.True(FillerWords.AreDefaults(settings.FillerWords));
        Assert.Null(settings.Current.Mcp);
        Assert.True(settings.McpEnabled);
        Assert.True(settings.Claude.IsServerOn);
        Assert.Equal((McpPermission.Allow, McpPermission.Ask, McpPermission.Ask),
            (settings.OpenFilesPermission, settings.SaveProjectPermission, settings.ExportPermission));

        File.WriteAllText(file, """{ "transcription": {}, "mcp": { "enabled": false, "export": "Sometimes", "openFiles": "Never", "saveProject": "2" } }""");
        settings.Load(new AppSettingsStore(file).Load());
        Assert.False(settings.McpEnabled);
        Assert.False(settings.Claude.IsServerOn);
        Assert.Equal((McpPermission.Allow, McpPermission.Ask, McpPermission.Ask),
            (settings.OpenFilesPermission, settings.SaveProjectPermission, settings.ExportPermission));
        Assert.Equal(new McpSettings(false, "Allow", "Ask", "Ask"), settings.Current.Mcp);
    }

    [AvaloniaFact]
    public void Permissions_are_saved_and_the_export_note_follows()
    {
        var store = new AppSettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = App.CreateEditor(null).Settings;
        settings.Store = store;
        Assert.Equal("HighlightCut shows a request with the clips, file and format. Nothing is written until you allow it.", settings.ExportPermissionNote);
        Assert.Equal(["Allow", "Ask"], settings.OpenFilesOptions.Select(o => o.Label));
        Assert.Equal(["Allow", "Ask", "Never"], settings.ExportOptions.Select(o => o.Label));

        settings.OpenFilesOptions.Single(o => o.Label == "Ask").PickCommand.Execute(null);
        settings.SaveProjectOptions.Single(o => o.Label == "Allow").PickCommand.Execute(null);
        settings.ExportOptions.Single(o => o.Label == "Never").PickCommand.Execute(null);

        Assert.Equal(new McpSettings(true, "Ask", "Allow", "Never"), store.Load().Mcp);
        Assert.Equal("Claude can prepare the timeline. Only you can export.", settings.ExportPermissionNote);
        Assert.Equal(["Never"], settings.ExportOptions.Where(o => o.IsSelected).Select(o => o.Label));
        Assert.Equal(["Ask"], settings.OpenFilesOptions.Where(o => o.IsSelected).Select(o => o.Label));

        settings.ExportPermission = McpPermission.Allow;
        Assert.Equal("Exports start right away. You can cancel them in the Claude panel.", settings.ExportPermissionNote);
        Assert.Equal(["Allow"], settings.ExportOptions.Where(o => o.IsSelected).Select(o => o.Label));
        Assert.Equal("Allow", store.Load().Mcp?.Export);

        settings.McpEnabled = false;
        Assert.False(store.Load().Mcp?.Enabled);
        Assert.Equal(McpStatus.Off, settings.Claude.Status);
    }

    // ---- MCP server ------------------------------------------------------------------------

    [AvaloniaFact]
    public void The_status_card_follows_the_connection()
    {
        var settings = App.CreateEditor(null).Settings;
        var claude = settings.Claude;
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        settings.Clock = () => now;
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        (string, string) Card() => (settings.McpStatusTitle, settings.McpStatusDetail);

        settings.McpEnabled = false;
        Assert.Equal(("Not running", "Claude can’t reach HighlightCut while this is off."), Card());
        Assert.True(settings.IsMcpDotOff);
        settings.McpEnabled = true;
        claude.IsListening = true;
        Assert.Equal(("Waiting for Claude", "The server is running. Add HighlightCut to Claude Code or Claude Desktop below, then start a chat."), Card());
        Assert.True(settings.IsMcpDotIdle);
        Assert.Contains(nameof(SettingsViewModel.McpStatusTitle), raised);

        claude.IsConnected = true;
        claude.ConnectedSince = now.AddMinutes(-12);
        claude.ClientName = "Claude Desktop";
        Assert.Equal(("Claude connected", "Claude Desktop · connected 12 min"), Card());
        Assert.True(settings.IsMcpDotConnected);
        claude.ClientName = null;
        Assert.Equal("Claude · connected 12 min", settings.McpStatusDetail);
        claude.ConnectedSince = now.AddMinutes(-75);
        Assert.Equal("Claude · connected 1 h 15 min", settings.McpStatusDetail);
        claude.ConnectedSince = now.AddHours(-2);
        Assert.Equal("Claude · connected 2 h", settings.McpStatusDetail);
        claude.ConnectedSince = now.AddSeconds(-30);
        Assert.Equal("Claude · connected just now", settings.McpStatusDetail);
        claude.ConnectedSince = null;
        Assert.Equal("Claude · connected", settings.McpStatusDetail);

        claude.ClientName = "Claude Desktop";
        claude.ConnectedSince = now.AddMinutes(-12);
        claude.NoteActivity();
        Assert.Equal(("Claude editing", "Claude Desktop · connected 12 min · editing the timeline"), Card());
        Assert.True(settings.IsMcpDotEditing);
        Assert.False(settings.IsMcpDotConnected);

        claude.IsConnected = false;
        claude.IsListening = false;
        claude.IsServedElsewhere = true;
        Assert.Equal(("Running in another window",
            "Another HighlightCut window has the server. Close that window to connect Claude here."), Card());
        claude.OtherWindowProject = "podcast_ep12.highlightcut";
        Assert.Equal("The HighlightCut window with podcast_ep12.highlightcut has the server. Close that window to connect Claude here.",
            settings.McpStatusDetail);
        Assert.True(settings.IsMcpDotIdle);

        settings.McpEnabled = false;
        Assert.Equal(McpStatus.Off, settings.ConnectionStatus);
    }

    [AvaloniaFact]
    public async Task Turning_the_server_off_stops_it_and_on_again_listens()
    {
        var editor = App.CreateEditor(null);
        var claude = editor.Claude;
        await using var server = new EditorMcpServer(editor, "highlightcut-app-test-" + Guid.NewGuid().ToString("N")[..12]);
        server.Start();
        await PumpUntil(() => claude.IsListening);
        Assert.Equal(McpStatus.Waiting, claude.Status);

        editor.Settings.McpEnabled = false;
        await PumpUntil(() => !claude.IsListening);
        Assert.Equal(McpStatus.Off, claude.Status);

        editor.Settings.McpEnabled = true;
        await PumpUntil(() => claude.IsListening);
        var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000, Ct);
        var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: Ct);
        await PumpUntil(() => claude.IsConnected);
        Assert.NotNull(claude.ConnectedSince);
        Assert.Equal("Claude connected", editor.Settings.McpStatusTitle);

        // Off ends Claude's session too.
        editor.Settings.McpEnabled = false;
        await PumpUntil(() => !claude.IsConnected && !claude.IsListening);
        Assert.Null(claude.ConnectedSince);
        await client.DisposeAsync();
        await pipe.DisposeAsync();

        editor.Settings.McpEnabled = true;
        await PumpUntil(() => claude.IsListening);
    }

    [AvaloniaFact]
    public async Task Copy_says_Copied_for_a_moment()
    {
        var settings = App.CreateEditor(null).Settings;
        string? copied = null;
        settings.CopyText = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await settings.CopyClaudeCodeCommand.ExecuteAsync(null);

        Assert.Equal(settings.ClaudeCodeCommand, copied);
        Assert.Equal("Copied", settings.ClaudeCodeCopyLabel);
        Assert.Equal("Copy", settings.ClaudeDesktopCopyLabel);
        await PumpUntil(() => settings.ClaudeCodeCopyLabel == "Copy");

        await settings.CopyClaudeDesktopCommand.ExecuteAsync(null);
        Assert.Equal(settings.ClaudeDesktopConfig, copied);
        Assert.Equal("Copied", settings.ClaudeDesktopCopyLabel);
    }

    [AvaloniaFact]
    public void The_desktop_config_keeps_args_on_one_line()
    {
        var settings = App.CreateEditor(null).Settings;
        Assert.Contains("\"args\": [\"mcp\"]", settings.ClaudeDesktopConfig, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(settings.ClaudeDesktopConfig);
        var entry = json.RootElement.GetProperty("mcpServers").GetProperty("highlightcut");
        Assert.Equal(settings.McpCommand, entry.GetProperty("command").GetString());
        Assert.Equal(["mcp"], entry.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
    }

    [AvaloniaFact]
    public void The_mcp_demo_matches_the_design()
    {
        var settings = App.CreateEditor(DesignScreen.SettingsMcp).Settings;

        Assert.True(settings.IsOpen);
        Assert.True(settings.IsMcp);
        Assert.Equal(("Claude connected", "Claude Desktop · connected 12 min"), (settings.McpStatusTitle, settings.McpStatusDetail));
        Assert.True(settings.IsMcpDotConnected);
        Assert.Equal(
            "{\n  \"mcpServers\": {\n    \"highlightcut\": {\n      \"command\": \"C:\\\\Program Files\\\\HighlightCut\\\\HighlightCut.exe\",\n      \"args\": [\"mcp\"]\n    }\n  }\n}",
            settings.ClaudeDesktopConfig);
        Assert.Equal(@"%APPDATA%\Claude\claude_desktop_config.json", settings.McpConfigPathText);
        Assert.Equal((McpPermission.Allow, McpPermission.Ask, McpPermission.Ask),
            (settings.OpenFilesPermission, settings.SaveProjectPermission, settings.ExportPermission));
        Assert.Equal("Copy", settings.ClaudeCodeCopyLabel);
    }
}
