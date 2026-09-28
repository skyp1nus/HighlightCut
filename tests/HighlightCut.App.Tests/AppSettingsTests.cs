using Avalonia.Headless.XUnit;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.Core.Transcripts;

namespace HighlightCut.App.Tests;

/// <summary>The settings file: every section round-trips, older files read as defaults, and a change keeps the rest.</summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-app-settings").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File => Path.Combine(_dir, "settings.json");

    /// <summary>Every section with values other than its defaults.</summary>
    private AppSettings Changed() => new(
        new TranscriptionSettings("Whisper", "best", "CPU", "Ukrainian", _dir, TranscribeOnOpen: true,
            FillerWords: new Dictionary<string, IReadOnlyList<string>> { ["en"] = ["um", "so"], ["uk"] = ["ну"] }),
        new GeneralSettings(StartupAction.StartEmpty, Autosave: false, RecentFilesLimit: 20),
        new PlaybackSettings(HardwareDecodingMode.Off, VideoRendererMode.Software, "Headphones", 5, RememberVolumeAndSpeed: false, 0.5, 1.5),
        new ExportDefaults(ExportDefaultMode.Reencode, ExportContainerDefault.Mkv, ExportFolderMode.Fixed, _dir, "{date}-{project}",
            Merge: false, Chapters: false, ExportAudioTracksMode.UnmutedOnly, ReencodeVideoPreset.H265, ReencodeAudioChoice.Aac192,
            UseGpuEncoder: false, FileExistsAction.Overwrite, AfterExportAction.Nothing),
        new KeyboardSettings(new Dictionary<string, IReadOnlyList<string>> { ["ToggleExclude"] = ["Ctrl E"], ["Export"] = [] }),
        new McpSettings(Enabled: false, "Ask", "Allow", "Never"),
        new TimelineSettings(Keyframes: false, Silences: false, Scenes: true, Snap: false, Frames: true));

    private static void AssertSame(AppSettings expected, AppSettings actual)
    {
        Assert.Equal(expected.Transcription with { FillerWords = null }, actual.Transcription with { FillerWords = null });
        AssertLists(expected.Transcription.FillerWords, actual.Transcription.FillerWords);
        Assert.Equal(expected.General, actual.General);
        Assert.Equal(expected.Playback, actual.Playback);
        Assert.Equal(expected.Export, actual.Export);
        AssertLists(expected.Keyboard?.Shortcuts, actual.Keyboard?.Shortcuts);
        Assert.Equal(expected.Mcp, actual.Mcp);
        Assert.Equal(expected.Timeline, actual.Timeline);
    }

    private static void AssertLists(IReadOnlyDictionary<string, IReadOnlyList<string>>? expected, IReadOnlyDictionary<string, IReadOnlyList<string>>? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }
        Assert.NotNull(actual);
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var (key, words) in expected)
            Assert.Equal(words, actual[key]);
    }

    [Fact]
    public void Every_section_round_trips_through_the_file()
    {
        var store = new AppSettingsStore(File);
        var settings = Changed();

        store.Save(settings);

        AssertSame(settings, store.Load());
        string json = System.IO.File.ReadAllText(File);
        Assert.Contains("\"startup\": \"StartEmpty\"", json, StringComparison.Ordinal);
        Assert.Contains("\"video\": \"H265\"", json, StringComparison.Ordinal);
        Assert.Contains("\"timeline\": {", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Transcribing_on_open_saved_while_it_was_the_default_reads_as_off()
    {
        System.IO.File.WriteAllText(File, """{ "transcription": { "transcribeOnOpen": true } }""");
        var store = new AppSettingsStore(File);

        Assert.False(store.Load().Transcription.TranscribeOnOpen);

        store.Save(AppSettings.Default with { Transcription = new TranscriptionSettings(TranscribeOnOpen: true) });
        Assert.Contains("\"transcribeWhenOpened\": true", System.IO.File.ReadAllText(File), StringComparison.Ordinal);
        Assert.True(store.Load().Transcription.TranscribeOnOpen);
    }

    [Fact]
    public void An_older_file_without_the_new_keys_reads_them_as_defaults()
    {
        System.IO.File.WriteAllText(File, """{ "transcription": { "engine": "Whisper", "language": "English" } }""");

        var loaded = new AppSettingsStore(File).Load();

        Assert.Equal(new TranscriptionSettings(Engine: "Whisper", Language: "English"), loaded.Transcription);
        Assert.False(loaded.Transcription.TranscribeOnOpen);
        Assert.Null(loaded.Transcription.FillerWords);
        Assert.Null(loaded.General);
        Assert.Null(loaded.Playback);
        Assert.Null(loaded.Export);
        Assert.Null(loaded.Keyboard);
        Assert.Null(loaded.Mcp);
        Assert.Null(loaded.Timeline);
        Assert.Equal(AppSettings.Default with { Transcription = loaded.Transcription }, loaded);
    }

    [Fact]
    public void An_unknown_choice_reads_as_its_default_and_keeps_the_rest()
    {
        System.IO.File.WriteAllText(File, """
            { "transcription": { "modelsFolder": "/m" }, "general": { "startup": "Sometimes", "autosave": false },
              "playback": { "renderer": "Vulkan", "hardwareDecoding": "off" }, "export": { "mode": 1, "container": 7, "video": null } }
            """);

        var loaded = new AppSettingsStore(File).Load();

        Assert.Equal("/m", loaded.Transcription.ModelsFolder);
        Assert.Equal(new GeneralSettings(StartupAction.OpenLastProject, Autosave: false), loaded.General);
        Assert.Equal(new PlaybackSettings(HardwareDecodingMode.Off, VideoRendererMode.Auto), loaded.Playback);
        Assert.Equal(new ExportDefaults(ExportDefaultMode.Reencode), loaded.Export);
    }

    [AvaloniaFact]
    public void A_hand_edited_file_with_nulls_still_loads()
    {
        System.IO.File.WriteAllText(File, """
            { "transcription": { "engine": null, "fillerWords": { "en": ["um", null, "so"], "de": null } },
              "keyboard": { "shortcuts": { "Export": ["Ctrl E", null], "Undo": null } }, "mcp": { "export": null } }
            """);
        var settings = App.CreateEditor(null).Settings;

        settings.Load(new AppSettingsStore(File).Load());

        Assert.Equal("Auto", settings.Engine);
        Assert.Equal(["um", "so"], settings.FillerWords["en"]);
        Assert.Equal(McpPermission.Ask, settings.ExportPermission);
    }

    [AvaloniaFact]
    public void A_change_in_one_section_saves_the_others_as_they_were()
    {
        var store = new AppSettingsStore(File);
        var settings = App.CreateEditor(null).Settings;
        var saved = Changed();
        settings.Load(saved);
        settings.Store = store;
        Assert.Equal("Whisper", settings.Engine);
        AssertSame(saved, settings.Current);

        settings.Device = "GPU";

        var reread = store.Load();
        Assert.Equal("GPU", reread.Transcription.Device);
        AssertSame(saved with { Transcription = saved.Transcription with { Device = "GPU" } }, reread);
    }

    [AvaloniaFact]
    public void Filler_words_start_as_the_defaults_and_are_saved_only_once_changed()
    {
        var store = new AppSettingsStore(File);
        var settings = App.CreateEditor(null).Settings;
        settings.Store = store;
        int changes = 0;
        settings.FillerWordsChanged += (_, _) => changes++;
        Assert.True(FillerWords.AreDefaults(settings.FillerWords));
        Assert.Null(settings.Current.Transcription.FillerWords);

        settings.SetFillerWords("en", [.. FillerWords.English, "  Hmm ", "UM"]);

        Assert.Equal(1, changes);
        Assert.Equal([.. FillerWords.English, "hmm"], settings.FillerWords["en"]);
        Assert.Equal(FillerWords.Ukrainian, settings.FillerWords["uk"]);
        var saved = store.Load().Transcription.FillerWords;
        Assert.NotNull(saved);
        Assert.Equal([.. FillerWords.English, "hmm"], saved["en"]);
        Assert.Equal(FillerWords.Ukrainian, saved["uk"]);

        settings.SetFillerWords("en", ["um", "uh", "er", "like", "you know", "hmm"]);
        Assert.Equal(1, changes);

        var reloaded = App.CreateEditor(null).Settings;
        int reloadChanges = 0;
        reloaded.FillerWordsChanged += (_, _) => reloadChanges++;
        reloaded.Load(store.Load());
        Assert.Equal(1, reloadChanges);
        Assert.Equal([.. FillerWords.English, "hmm"], reloaded.FillerWords["en"]);

        settings.SetFillerWords("en", FillerWords.English);
        Assert.Equal(2, changes);
        Assert.Null(store.Load().Transcription.FillerWords);
    }

    [AvaloniaFact]
    public void The_demo_shows_the_default_filler_words()
    {
        var editor = App.CreateEditor(null);
        editor.Settings.SetFillerWords("uk", ["ну"]);
        int changes = 0;
        editor.Settings.FillerWordsChanged += (_, _) => changes++;

        editor.Settings.LoadDemo();

        Assert.True(FillerWords.AreDefaults(editor.Settings.FillerWords));
        Assert.Equal(1, changes);
    }

    [AvaloniaFact]
    public void Download_progress_is_reported_in_bytes()
    {
        var model = App.CreateEditor(null).Settings.Models[0];

        model.ReportBytes(HighlightCut.Transcription.Models.InstallPhase.Downloading, 212_000_000, 488_000_000);
        Assert.Equal((212_000_000L, (long?)488_000_000, false), (model.ReceivedBytes, model.TotalBytes, model.IsUnpacking));

        model.ReportBytes(HighlightCut.Transcription.Models.InstallPhase.Unpacking, 488_000_000, 488_000_000);
        Assert.True(model.IsUnpacking);
    }

    [AvaloniaFact]
    public void A_new_download_starts_its_byte_count_at_zero()
    {
        var settings = App.CreateEditor(Demo.DesignScreen.Settings).Settings;
        var model = settings.Models.First(m => m.IsInstalled);
        model.DeleteCommand.Execute(null);
        model.ReportBytes(HighlightCut.Transcription.Models.InstallPhase.Unpacking, 5, 5);

        model.DownloadCommand.Execute(null);

        Assert.True(model.IsDownloading);
        Assert.Equal((0L, (long?)null, false), (model.ReceivedBytes, model.TotalBytes, model.IsUnpacking));
    }
}
