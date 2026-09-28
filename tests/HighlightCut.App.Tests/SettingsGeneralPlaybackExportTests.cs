using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.Demo;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;
using HighlightCut.App.Views.Settings;
using HighlightCut.Core.Serialization;
using HighlightCut.Media.Export;

namespace HighlightCut.App.Tests;

/// <summary>Settings → General, Playback and Export: saved choices, what they change in the editor, and the file name preview.</summary>
public sealed class SettingsGeneralPlaybackExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-settings-gpe").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string SettingsFile => Path.Combine(_dir, "settings.json");

    private static void Pick(IReadOnlyList<ChoiceOption> options, string label) =>
        options.Single(o => o.Label == label).PickCommand.Execute(null);

    private static string Selected(IReadOnlyList<ChoiceOption> options) => options.Single(o => o.IsSelected).Label;

    private sealed class FolderDialogs(string folder) : IFileDialogs
    {
        public Task<string?> PickMediaToOpenAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickProjectToOpenAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickProjectSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title, string? startFolder) => Task.FromResult<string?>(folder);
    }

    [AvaloniaFact]
    public async Task Re_encoding_uses_the_gpu_encoder_found_at_start_when_allowed()
    {
        var editor = App.CreateEditor(null);
        var settings = editor.Settings;
        var export = editor.Export;
        Assert.Equal("No GPU encoder found. Re-encoding uses the CPU.", settings.GpuEncoderNote);

        await settings.DetectGpuEncoderAsync(_ => Task.FromResult<GpuEncoderSupport?>(new(GpuEncoder.Nvenc, Hevc: false)), TestContext.Current.CancellationToken);

        Assert.Equal("Detected: NVIDIA NVENC. H.265 still uses the CPU.", settings.GpuEncoderNote);
        Assert.Null(export.BuildSettings().GpuEncoder);
        export.Mode = ExportMode.Encode;
        Assert.Same(GpuEncoder.Nvenc, export.BuildSettings().GpuEncoder);
        Assert.EndsWith("H.264 re-encode on NVIDIA NVENC", export.EstimateLine, StringComparison.Ordinal);
        export.Video = VideoEncoding.H265;
        Assert.Null(export.BuildSettings().GpuEncoder);
        Assert.EndsWith("H.265 re-encode", export.EstimateLine, StringComparison.Ordinal);

        export.Video = VideoEncoding.H264Quality;
        settings.UseGpuEncoder = false;
        Assert.Null(export.BuildSettings().GpuEncoder);

        await settings.DetectGpuEncoderAsync(_ => Task.FromResult<GpuEncoderSupport?>(new(GpuEncoder.Amf, Hevc: true)), TestContext.Current.CancellationToken);
        Assert.Equal("Detected: AMD AMF", settings.GpuEncoderNote);
        await settings.DetectGpuEncoderAsync(_ => Task.FromResult<GpuEncoderSupport?>(null), TestContext.Current.CancellationToken);
        Assert.Equal("No GPU encoder found. Re-encoding uses the CPU.", settings.GpuEncoderNote);
        Assert.Null(export.Gpu);
    }

    [AvaloniaFact]
    public void General_playback_and_export_choices_are_saved_and_read_back()
    {
        var store = new AppSettingsStore(SettingsFile);
        var settings = App.CreateEditor(null).Settings;
        settings.Store = store;

        Pick(settings.StartupOptions, "Start empty");
        settings.Autosave = false;
        Pick(settings.RecentLimitOptions, "20");
        Pick(settings.HardwareDecodingOptions, "Off");
        Pick(settings.RendererOptions, "Software");
        Pick(settings.JumpOptions, "5 s");
        settings.RememberVolumeAndSpeed = false;
        Pick(settings.ExportModeOptions, "Re-encode");
        Pick(settings.ContainerOptions, "MKV");
        Pick(settings.ExportFolderOptions, "Fixed folder");
        settings.FixedExportFolder = _dir;
        settings.FileNamePattern = "{date}-{project}";
        settings.MergeFiles = false;
        settings.ChaptersFromLabels = false;
        Pick(settings.AudioTrackOptions, "Only unmuted lanes");
        Pick(settings.ReencodeVideoOptions, "H.265");
        Pick(settings.ReencodeAudioOptions, "AAC 192 kbps");
        settings.UseGpuEncoder = false;
        Pick(settings.IfFileExistsOptions, "Overwrite");
        Pick(settings.AfterExportOptions, "Nothing");

        var saved = store.Load();
        Assert.Equal(new GeneralSettings(StartupAction.StartEmpty, Autosave: false, RecentFilesLimit: 20), saved.General);
        Assert.Equal(new PlaybackSettings(HardwareDecodingMode.Off, VideoRendererMode.Software, null, 5, RememberVolumeAndSpeed: false),
            saved.Playback);
        var export = new ExportDefaults(ExportDefaultMode.Reencode, ExportContainerDefault.Mkv, ExportFolderMode.Fixed, _dir, "{date}-{project}",
            Merge: false, Chapters: false, ExportAudioTracksMode.UnmutedOnly, ReencodeVideoPreset.H265, ReencodeAudioChoice.Aac192,
            UseGpuEncoder: false, FileExistsAction.Overwrite, AfterExportAction.Nothing);
        Assert.Equal(export, saved.Export);

        var editor = App.CreateEditor(null);
        var reloaded = editor.Settings;
        reloaded.Load(store.Load());
        Assert.Equal((StartupAction.StartEmpty, false, 20), (reloaded.Startup, reloaded.Autosave, reloaded.RecentFilesLimit));
        Assert.Equal(("Start empty", "20"), (Selected(reloaded.StartupOptions), Selected(reloaded.RecentLimitOptions)));
        Assert.Equal(("Off", "Software", "5 s"), (Selected(reloaded.HardwareDecodingOptions), Selected(reloaded.RendererOptions),
            Selected(reloaded.JumpOptions)));
        Assert.False(reloaded.RememberVolumeAndSpeed);
        Assert.Equal(["Re-encode", "MKV", "Fixed folder", "Only unmuted lanes", "H.265", "AAC 192 kbps", "Overwrite", "Nothing"],
            [Selected(reloaded.ExportModeOptions), Selected(reloaded.ContainerOptions), Selected(reloaded.ExportFolderOptions),
                Selected(reloaded.AudioTrackOptions), Selected(reloaded.ReencodeVideoOptions), Selected(reloaded.ReencodeAudioOptions),
                Selected(reloaded.IfFileExistsOptions), Selected(reloaded.AfterExportOptions)]);
        Assert.Equal(export, reloaded.ToExportDefaults());
        Assert.Equal(export, editor.Export.Defaults);
        Assert.False(editor.AutosaveEnabled);
        Assert.Equal(5, editor.JumpSeconds);
    }

    [AvaloniaFact]
    public void An_older_settings_file_reads_the_new_sections_as_defaults()
    {
        File.WriteAllText(SettingsFile, """{ "transcription": { "engine": "Whisper" } }""");
        var editor = App.CreateEditor(null);
        var settings = editor.Settings;

        settings.Load(new AppSettingsStore(SettingsFile).Load());

        Assert.Equal("Whisper", settings.Engine);
        Assert.Equal(new GeneralSettings(), settings.ToGeneralSettings());
        Assert.Equal(new PlaybackSettings(), settings.ToPlaybackSettings());
        Assert.Equal(new ExportDefaults(), settings.ToExportDefaults());
        Assert.Equal(("Open the last project", "Auto", "1 s", "Lossless"), (Selected(settings.StartupOptions),
            Selected(settings.HardwareDecodingOptions), Selected(settings.JumpOptions), Selected(settings.ExportModeOptions)));
        // Sections the file does not have stay out of it until they change.
        Assert.Null(settings.Current.General);
        Assert.Null(settings.Current.Playback);
        Assert.Null(settings.Current.Export);
    }

    [AvaloniaFact]
    public void Unknown_values_fall_back_to_the_defaults()
    {
        var settings = App.CreateEditor(null).Settings;

        settings.Load(AppSettings.Default with
        {
            General = new GeneralSettings((StartupAction)7, RecentFilesLimit: 7),
            Playback = new PlaybackSettings(JumpSeconds: 3, AudioDevice: "USB headset", Volume: 0.4),
            Export = new ExportDefaults(FileNamePattern: "  ", Video: (ReencodeVideoPreset)9, FixedFolder: " "),
        });

        Assert.Equal((StartupAction.OpenLastProject, 10), (settings.Startup, settings.RecentFilesLimit));
        Assert.Equal(1, settings.JumpSeconds);
        Assert.Equal(ExportFileNames.DefaultPattern, settings.FileNamePattern);
        Assert.Equal(ReencodeVideoPreset.H264Quality, settings.ReencodeVideo);
        Assert.Equal(ExportDefaults.DefaultFixedFolder, settings.FixedExportFolder);
        // A device that is not listed now stays chosen and shown.
        Assert.Equal("USB headset", settings.AudioDevice);
        Assert.Contains("USB headset", settings.AudioDevices);
        // What is saved next is what is shown; the remembered volume stays.
        Assert.Equal(new GeneralSettings(), settings.Current.General);
        Assert.Equal(new PlaybackSettings(JumpSeconds: 1, AudioDevice: "USB headset", Volume: 0.4), settings.Current.Playback);
        Assert.Equal(new ExportDefaults(), settings.Current.Export);
    }

    [AvaloniaFact]
    public void Jump_length_sets_how_far_shift_arrows_move()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        double t0 = editor.Time;

        Pick(editor.Settings.JumpOptions, "5 s");
        Shortcuts.Handle(editor, Key.Right, KeyModifiers.Shift);
        Assert.Equal(t0 + 5, editor.Time, 6);

        Pick(editor.Settings.JumpOptions, "0.5 s");
        Shortcuts.Handle(editor, Key.Left, KeyModifiers.Shift);
        Assert.Equal(t0 + 4.5, editor.Time, 6);
    }

    [AvaloniaFact]
    public async Task With_autosave_off_edits_are_not_saved()
    {
        string path = Path.Combine(_dir, "keynote" + ProjectFile.Extension);
        var editor = App.CreateEditor(null);
        DemoScenario.OpenSample(editor);
        await editor.SaveToAsync(path, auto: false);
        editor.AutosaveDelay = TimeSpan.FromMilliseconds(10);
        editor.Settings.Autosave = false;

        editor.Select(editor.Clips[1]);
        editor.ToggleExclude();
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(editor.IsDirty);
        Assert.True((await ProjectFile.LoadAsync(path, TestContext.Current.CancellationToken)).Get(editor.Clips[1].Id).IsIncluded);

        editor.Settings.Autosave = true;
        for (int i = 0; i < 100 && editor.IsDirty; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.False(editor.IsDirty);
        Assert.False((await ProjectFile.LoadAsync(path, TestContext.Current.CancellationToken)).Get(editor.Clips[1].Id).IsIncluded);
    }

    [AvaloniaFact]
    public void The_export_dialog_starts_with_the_defaults()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        var settings = editor.Settings;
        Pick(settings.ExportModeOptions, "Re-encode");
        Pick(settings.ContainerOptions, "MOV");
        settings.MergeFiles = false;
        settings.ChaptersFromLabels = false;
        Pick(settings.AudioTrackOptions, "Only unmuted lanes");
        Pick(settings.ReencodeVideoOptions, "H.265");
        Pick(settings.ReencodeAudioOptions, "AAC 192 kbps");

        var export = editor.Export;
        export.Open();

        Assert.Equal(ExportMode.Encode, export.Mode);
        Assert.Equal("MOV", export.Container);
        Assert.False(export.Merge);
        Assert.False(export.AddChapters);
        Assert.False(export.KeepAllTracks);
        Assert.Equal(VideoEncoding.H265, export.Video);
        Assert.Same(export.AudioChoices[^1], export.Audio);
    }

    [AvaloniaFact]
    public void Changing_the_defaults_does_not_touch_an_open_dialog()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        var export = editor.Export;
        export.Open();
        export.Merge = false;

        Pick(editor.Settings.ExportModeOptions, "Re-encode");
        Assert.Equal(ExportMode.Copy, export.Mode);
        Assert.False(export.Merge);

        export.Close();
        export.Open();
        Assert.Equal(ExportMode.Encode, export.Mode);
        Assert.True(export.Merge);

        // A choice made in the dialog survives reopening while the defaults stay the same.
        export.Mode = ExportMode.Copy;
        export.Close();
        export.Open();
        Assert.Equal(ExportMode.Copy, export.Mode);
    }

    [Fact]
    public void Container_and_folder_defaults()
    {
        var same = new ExportDefaults();
        Assert.Equal("MOV", same.ContainerFor("mov"));
        Assert.Equal("MKV", same.ContainerFor("webm"));
        Assert.Equal("MP4", same.ContainerFor(null));
        Assert.Equal("MKV", new ExportDefaults(Container: ExportContainerDefault.Mkv).ContainerFor("mp4"));

        string source = Path.Combine(Path.GetTempPath(), "talks", "talk.mp4");
        Assert.Equal(Path.GetDirectoryName(source), same.FolderFor(source));
        string fixedFolder = Path.Combine(Path.GetTempPath(), "exports");
        Assert.Equal(fixedFolder, new ExportDefaults(Folder: ExportFolderMode.Fixed, FixedFolder: fixedFolder).FolderFor(source));
        Assert.Equal(ExportDefaults.DefaultFixedFolder, new ExportDefaults(Folder: ExportFolderMode.Fixed).FolderFor(source));
    }

    [AvaloniaFact]
    public void The_file_name_preview_follows_the_pattern_and_container()
    {
        var settings = App.CreateEditor(DesignScreen.SettingsExport).Settings;
        Assert.Equal("interview_final_v3-cut.mp4", settings.MergedNamePreview);
        Assert.Equal(["interview_final_v3-cut-01.mp4", "interview_final_v3-cut-02.mp4", "interview_final_v3-cut-03.mp4",
            "interview_final_v3-cut-04.mp4"], settings.SeparateNamePreview);

        settings.FileNamePattern = "{date}_{label}";
        Pick(settings.ContainerOptions, "MKV");

        Assert.Equal("2026-09-25.mkv", settings.MergedNamePreview);
        Assert.Equal("2026-09-25_cold-open.mkv", settings.SeparateNamePreview[0]);
        Assert.Equal("2026-09-25_outro.mkv", settings.SeparateNamePreview[^1]);
    }

    [AvaloniaFact]
    public void With_a_file_open_the_preview_uses_its_name_and_clips()
    {
        var editor = App.CreateEditor(null);
        DemoScenario.OpenSample(editor);
        var settings = editor.Settings;
        settings.FileNamePattern = "{project} {date} {label}";

        settings.Open();

        string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal($"{editor.ProjectName} {today}.mp4", settings.MergedNamePreview);
        Assert.Equal(editor.Clips.Count(c => c.IsIncluded), settings.SeparateNamePreview.Count);
        Assert.Equal($"{editor.ProjectName} {today} cold-open.mp4", settings.SeparateNamePreview[0]);
    }

    [AvaloniaFact]
    public void A_token_replaces_the_selection()
    {
        var settings = App.CreateEditor(null).Settings;

        Assert.Equal(6, settings.InsertPatternToken("{date}", 0, 9));
        Assert.Equal("{date}-cut-{n}", settings.FileNamePattern);

        Assert.Equal("{date}-cut-{n}{label}".Length, settings.InsertPatternToken("{label}", 99, 99));
        Assert.Equal("{date}-cut-{n}{label}", settings.FileNamePattern);

        // A backwards selection counts from its start.
        Assert.Equal(3, settings.InsertPatternToken("{n}", 6, 0));
        Assert.Equal("{n}-cut-{n}{label}", settings.FileNamePattern);
    }

    [AvaloniaFact]
    public void Clearing_in_the_demo_updates_the_labels()
    {
        var settings = App.CreateEditor(DesignScreen.SettingsGeneral).Settings;
        Assert.Equal(("1.8 GB · 42 videos", "Clear list"), (settings.CacheUsedText, settings.RecentLinkText));

        settings.ClearCacheCommand.Execute(null);
        settings.ClearRecentFilesCommand.Execute(null);

        Assert.Equal("0 B · 0 videos", settings.CacheUsedText);
        Assert.False(settings.CanClearCache);
        Assert.Equal("List cleared", settings.RecentLinkText);
    }

    [AvaloniaFact]
    public void Clear_list_forgets_the_recent_files()
    {
        var store = new RecentFilesStore(Path.Combine(_dir, "recent.json"));
        foreach (string name in (string[])["a.mp4", "b.mp4"])
        {
            string file = Path.Combine(_dir, name);
            File.WriteAllText(file, "");
            store.Add(file, 1);
        }
        var editor = App.CreateEditor(null, recent: store);
        editor.LoadRecentFiles();
        Assert.Equal(2, editor.RecentFiles.Count);

        editor.Settings.ClearRecentFilesCommand.Execute(null);

        Assert.Empty(store.Load());
        Assert.Empty(editor.RecentFiles);
        Assert.Equal("List cleared", editor.Settings.RecentLinkText);
    }

    [AvaloniaFact]
    public void The_recent_files_limit_sets_how_many_are_listed()
    {
        var store = new RecentFilesStore(Path.Combine(_dir, "recent.json"));
        for (int i = 0; i < 12; i++)
        {
            string file = Path.Combine(_dir, $"{i}.mp4");
            File.WriteAllText(file, "");
            store.Add(file, 1);
        }
        var editor = App.CreateEditor(null, recent: store);
        editor.LoadRecentFiles();
        Assert.Equal(10, editor.RecentFiles.Count);

        Pick(editor.Settings.RecentLimitOptions, "5");
        Assert.Equal(5, editor.RecentFiles.Count);
        Pick(editor.Settings.RecentLimitOptions, "20");
        Assert.Equal(12, editor.RecentFiles.Count);
        Assert.Equal("11.mp4", editor.RecentFiles[0].Name);
    }

    [AvaloniaFact]
    public async Task On_startup_the_last_project_opens_unless_a_file_was_given_or_the_setting_says_empty()
    {
        var store = new RecentFilesStore(Path.Combine(_dir, "recent.json"));
        foreach (string name in (string[])["older.mp4", "last.mp4"])
        {
            string file = Path.Combine(_dir, name);
            File.WriteAllText(file, "");
            store.Add(file, 1);
        }

        var opener = new SampleOpener();
        var editor = App.CreateEditor(null, opener, store);
        await editor.StartAsync(null);
        Assert.Equal([Path.Combine(_dir, "last.mp4")], opener.Opened);
        Assert.Equal("last.mp4", editor.MediaFileName);

        opener = new SampleOpener();
        editor = App.CreateEditor(null, opener, store);
        await editor.StartAsync("/videos/given.mp4");
        Assert.Equal(["/videos/given.mp4"], opener.Opened);

        opener = new SampleOpener();
        editor = App.CreateEditor(null, opener, store);
        editor.Settings.Startup = StartupAction.StartEmpty;
        await editor.StartAsync(null);
        Assert.Empty(opener.Opened);
        Assert.False(editor.HasFile);
    }

    [AvaloniaFact]
    public async Task Clear_cache_keeps_the_open_video_and_the_transcripts()
    {
        string cacheRoot = Path.Combine(_dir, "cache");
        string video = Path.Combine(_dir, "open.mp4");
        File.WriteAllText(video, "video");
        string openKey = HighlightCut.Media.Caching.MediaCache.KeyFor(video)!;
        void Put(string folder, string name, int bytes)
        {
            Directory.CreateDirectory(Path.Combine(cacheRoot, folder));
            File.WriteAllBytes(Path.Combine(cacheRoot, folder, name), new byte[bytes]);
        }
        Put(openKey, "keyframes.bin", 1000);
        Put("other1", "thumbs-54.bin", 2_000_000);
        Put("other1", "transcript-parakeet-auto.json", 500);
        Put("other2", "waveform.bin", 3000);

        var editor = App.CreateEditor(null, new SampleOpener());
        await editor.OpenMediaAsync(video);
        var settings = editor.Settings;
        settings.CacheFolder = cacheRoot;
        await settings.MeasureCacheAsync();
        Assert.Equal("2 MB · 3 videos", settings.CacheUsedText);
        Assert.True(settings.CanClearCache);

        await settings.ClearCacheCommand.ExecuteAsync(null);

        Assert.Equal("2 KB · 2 videos", settings.CacheUsedText);
        Assert.True(File.Exists(Path.Combine(cacheRoot, openKey, "keyframes.bin")));
        Assert.True(File.Exists(Path.Combine(cacheRoot, "other1", "transcript-parakeet-auto.json")));
        Assert.False(File.Exists(Path.Combine(cacheRoot, "other1", "thumbs-54.bin")));
        Assert.False(Directory.Exists(Path.Combine(cacheRoot, "other2")));

        settings.CacheFolder = Path.Combine(_dir, "no-cache");
        await settings.MeasureCacheAsync();
        Assert.Equal("0 B · 0 videos", settings.CacheUsedText);
        Assert.False(settings.CanClearCache);
    }

    [AvaloniaFact]
    public async Task Volume_and_speed_are_remembered_between_sessions()
    {
        var store = new AppSettingsStore(SettingsFile);
        var editor = App.CreateEditor(null);
        editor.Settings.Store = store;

        editor.Volume = 0.3;
        editor.Speed = 1.5;
        for (int i = 0; i < 100 && store.Load().Playback?.Speed is null; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(0.3, store.Load().Playback?.Volume);
        Assert.Equal(1.5, store.Load().Playback?.Speed);
        var next = App.CreateEditor(null);
        next.Settings.Load(store.Load());
        Assert.Equal((0.3, 1.5), (next.Volume, next.Speed));

        editor.Settings.RememberVolumeAndSpeed = false;
        Assert.Null(store.Load().Playback?.Volume);
        var fresh = App.CreateEditor(null);
        fresh.Settings.Load(store.Load());
        Assert.Equal((0.7, 1.0), (fresh.Volume, fresh.Speed));
    }

    [AvaloniaFact]
    public async Task Copy_diagnostics_copies_and_says_so_briefly()
    {
        var settings = App.CreateEditor(DesignScreen.SettingsGeneral).Settings;
        string? copied = null;
        settings.CopyText = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await settings.CopyDiagnosticsCommand.ExecuteAsync(null);

        Assert.NotNull(copied);
        Assert.Contains("HighlightCut 0.9.0", copied, StringComparison.Ordinal);
        Assert.Contains("ffmpeg 7.1", copied, StringComparison.Ordinal);
        Assert.Contains("libmpv 0.39.0", copied, StringComparison.Ordinal);
        Assert.Equal("Copied", settings.DiagnosticsButtonText);
        for (int i = 0; i < 150 && settings.DiagnosticsCopied; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal("Copy diagnostics", settings.DiagnosticsButtonText);
    }

    [Fact]
    public void Cache_usage_is_formatted_like_the_design()
    {
        Assert.Equal("1.8 GB · 42 videos", SettingsViewModel.FormatCacheUsage(1_800_000_000, 42));
        Assert.Equal("0 B · 0 videos", SettingsViewModel.FormatCacheUsage(0, 0));
        Assert.Equal("12 MB · 1 video", SettingsViewModel.FormatCacheUsage(12_400_000, 1));
        Assert.Equal("640 KB · 2 videos", SettingsViewModel.FormatCacheUsage(640_000, 2));
    }

    [Theory]
    [InlineData("mpv v0.39.0", "0.39.0")]
    [InlineData("mpv 0.38.0-dirty Copyright © 2000-2024", "0.38.0-dirty")]
    [InlineData("v0.40.0", "0.40.0")]
    [InlineData("", null)]
    public void The_libmpv_version_is_read_from_mpv_version(string text, string? version) =>
        Assert.Equal(version, SettingsViewModel.MpvVersion(text));

    [Fact]
    public void Hardware_decoding_maps_to_mpv()
    {
        Assert.Equal("no", new PlaybackSettings(HardwareDecodingMode.Off).MpvHardwareDecoding());
        Assert.Equal("auto-copy", new PlaybackSettings().MpvHardwareDecoding());
    }

    [AvaloniaFact]
    public async Task Change_picks_the_fixed_export_folder()
    {
        var store = new AppSettingsStore(SettingsFile);
        var editor = App.CreateEditor(null);
        editor.Settings.Store = store;
        editor.Dialogs = new FolderDialogs(_dir);
        Pick(editor.Settings.ExportFolderOptions, "Fixed folder");
        Assert.True(editor.Settings.IsFixedFolder);

        await editor.Settings.ChangeExportFolderCommand.ExecuteAsync(null);

        Assert.Equal(_dir, editor.Settings.FixedExportFolder);
        Assert.Equal(_dir, store.Load().Export?.FixedFolder);
        Assert.Equal(_dir, editor.Export.Defaults.FixedFolder);
    }

    private static (MainWindow Window, EditorViewModel Editor, ExportSection Section) OpenExportSection()
    {
        var editor = App.CreateEditor(DesignScreen.SettingsExport);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return (window, editor, window.GetVisualDescendants().OfType<ExportSection>().Single());
    }

    [AvaloniaFact]
    public void Clicking_a_token_inserts_it_at_the_caret()
    {
        var (window, editor, section) = OpenExportSection();
        var box = section.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PatternBox");
        box.Focus();
        box.SelectionStart = 0;
        box.SelectionEnd = 9;

        var date = section.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("token") && Equals(b.Content, "{date}"));
        date.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("{date}-cut-{n}", editor.Settings.FileNamePattern);
        Assert.Equal("{date}-cut-{n}", box.Text);
        Assert.Equal(6, box.CaretIndex);

        // Without a selection since, the next token goes to the caret.
        var n = section.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("token") && Equals(b.Content, "{n}"));
        n.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("{date}{n}-cut-{n}", editor.Settings.FileNamePattern);
        window.Close();
    }

    [AvaloniaFact]
    public void Fixed_folder_shows_the_path_row()
    {
        var (window, editor, section) = OpenExportSection();
        var row = section.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "FixedFolderRow");
        Assert.False(row.IsEffectivelyVisible);

        Pick(editor.Settings.ExportFolderOptions, "Fixed folder");
        Dispatcher.UIThread.RunJobs();

        Assert.True(row.IsEffectivelyVisible);
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == @"D:\Videos\Exports");
        window.Close();
    }
}
