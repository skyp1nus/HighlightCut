using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.App.Services;
using HighlightCut.Media;
using HighlightCut.Media.Caching;

namespace HighlightCut.App.ViewModels;

// Settings → General: startup, autosave, recent files, cache, versions and support.
public sealed partial class SettingsViewModel
{
    private DispatcherTimer? _copiedTimer;
    private bool _versionsRead;

    private ChoiceSet<StartupAction>? _startupChoices;
    private ChoiceSet<StartupAction> StartupChoices => _startupChoices ??= new(
        [(StartupAction.OpenLastProject, "Open the last project"), (StartupAction.StartEmpty, "Start empty")], v => Startup = v, Startup);
    public IReadOnlyList<ChoiceOption> StartupOptions => StartupChoices.Options;

    private ChoiceSet<int>? _recentLimitChoices;
    private ChoiceSet<int> RecentLimitChoices => _recentLimitChoices ??= new(
        [(5, "5"), (10, "10"), (20, "20")], v => RecentFilesLimit = v, RecentFilesLimit);
    public IReadOnlyList<ChoiceOption> RecentLimitOptions => RecentLimitChoices.Options;

    [ObservableProperty]
    public partial StartupAction Startup { get; set; } = StartupAction.OpenLastProject;

    [ObservableProperty]
    public partial bool Autosave { get; set; } = true;

    [ObservableProperty]
    public partial int RecentFilesLimit { get; set; } = 10;

    /// <summary>"Clear list" was clicked (the link then says so).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecentLinkText))]
    public partial bool IsRecentListCleared { get; set; }

    public string RecentLinkText => IsRecentListCleared ? "List cleared" : "Clear list";

    [ObservableProperty]
    public partial string CacheFolder { get; set; } = AppDataFolder.PathFor("cache");

    /// <summary>"1.8 GB · 42 videos"; "—" until it can be measured.</summary>
    [ObservableProperty]
    public partial string CacheUsedText { get; set; } = "—";

    [ObservableProperty]
    public partial bool CanClearCache { get; set; } = true;

    [ObservableProperty]
    public partial string AppVersion { get; set; } = ReadAppVersion();

    [ObservableProperty]
    public partial string FfmpegVersion { get; set; } = "…";

    [ObservableProperty]
    public partial string LibmpvVersion { get; set; } = "…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiagnosticsButtonText))]
    public partial bool DiagnosticsCopied { get; set; }

    public string DiagnosticsButtonText => DiagnosticsCopied ? "Copied" : "Copy diagnostics";

    partial void OnStartupChanged(StartupAction value)
    {
        _startupChoices?.Select(value);
        SaveGeneral();
    }

    partial void OnAutosaveChanged(bool value)
    {
        _editor.AutosaveEnabled = value;
        SaveGeneral();
    }

    partial void OnRecentFilesLimitChanged(int value)
    {
        _recentLimitChoices?.Select(value);
        if (_editor.RecentStore is { } store)
        {
            store.Limit = value;
            _editor.LoadRecentFiles();
        }
        SaveGeneral();
    }

    private void SaveGeneral() => UpdateSettings(s => s with { General = ToGeneralSettings() });

    internal GeneralSettings ToGeneralSettings() => new(Startup, Autosave, RecentFilesLimit);

    /// <summary>Shows <paramref name="s"/>; a value the dialog does not offer reads as its default.</summary>
    internal void LoadGeneral(GeneralSettings? s)
    {
        s ??= new();
        Startup = Enum.IsDefined(s.Startup) ? s.Startup : StartupAction.OpenLastProject;
        Autosave = s.Autosave;
        RecentFilesLimit = s.RecentFilesLimit is 5 or 10 or 20 ? s.RecentFilesLimit : 10;
        _editor.AutosaveEnabled = Autosave;
    }

    [RelayCommand]
    private void ClearRecentFiles()
    {
        _editor.RecentStore?.Clear();
        _editor.RecentFiles.Clear();
        IsRecentListCleared = true;
    }

    [RelayCommand]
    private void OpenCacheFolder()
    {
        if (!_editor.IsDemo)
            Reveal(CacheFolder);
    }

    /// <summary>Frees the cache but for the open video's analysis and every transcript, then measures it again.</summary>
    [RelayCommand]
    private async Task ClearCache()
    {
        if (_editor.IsDemo)
        {
            CacheUsedText = "0 B · 0 videos";
            CanClearCache = false;
            return;
        }
        CanClearCache = false;
        var cache = new MediaCache(CacheFolder);
        string? open = _editor.HasFile ? _editor.Session.Project.Source?.Path : null;
        await Task.Run(() => cache.Clear(open)).ConfigureAwait(true);
        await MeasureCacheAsync().ConfigureAwait(true);
    }

    /// <summary>Sums the cache's files off the UI thread (it can hold thousands of thumbnails).</summary>
    internal async Task MeasureCacheAsync()
    {
        var cache = new MediaCache(CacheFolder);
        var (bytes, videos) = await Task.Run(cache.Measure).ConfigureAwait(true);
        CacheUsedText = FormatCacheUsage(bytes, videos);
        CanClearCache = bytes > 0;
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        if (!_editor.IsDemo)
            Reveal(CrashLog.Folder);
    }

    private static void Reveal(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            FileManager.Reveal(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing to show.
        }
    }

    /// <summary>Copies versions and paths for a bug report; the button says "Copied" for 1.5 s.</summary>
    [RelayCommand]
    private async Task CopyDiagnostics()
    {
        await (CopyText?.Invoke(DiagnosticsText()) ?? Task.CompletedTask).ConfigureAwait(true);
        DiagnosticsCopied = true;
        _copiedTimer?.Stop();
        _copiedTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) =>
        {
            _copiedTimer?.Stop();
            _copiedTimer = null;
            DiagnosticsCopied = false;
        });
        _copiedTimer.Start();
    }

    internal string DiagnosticsText() => string.Join(Environment.NewLine,
        $"HighlightCut {AppVersion}",
        $"ffmpeg {FfmpegVersion}",
        $"libmpv {LibmpvVersion}",
        $"{RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture}",
        RuntimeInformation.FrameworkDescription,
        $"Hardware decoding {HardwareDecoding} · renderer {Renderer}",
        $"Video output {_editor.VideoOutput ?? "not started"} · decoder {_editor.Player?.CurrentDecoder ?? "—"}",
        $"Open file {_editor.MediaInfoText}",
        $"Analysis {_editor.Media?.AnalysisTimes ?? "—"}",
        $"Timeline chips {_editor.ChipSummary} (a file is analysed only for what is on)",
        $"Settings {AppSettingsStore.DefaultFile}",
        $"Logs {CrashLog.Folder}");

    /// <summary>"1.8 GB · 42 videos" (SI units, like the design).</summary>
    internal static string FormatCacheUsage(long bytes, int videos)
    {
        var c = CultureInfo.InvariantCulture;
        string size = bytes switch
        {
            < 1_000 => bytes.ToString(c) + " B",
            < 1_000_000 => (bytes / 1e3).ToString("0", c) + " KB",
            < 1_000_000_000 => (bytes / 1e6).ToString("0", c) + " MB",
            _ => (bytes / 1e9).ToString("0.0", c) + " GB",
        };
        return $"{size} · {videos.ToString(c)} {(videos == 1 ? "video" : "videos")}";
    }

    /// <summary>Measures the cache and reads the tool versions (once); the demo keeps its sample values.</summary>
    private void RefreshGeneral()
    {
        if (_editor.IsDemo)
            return;
        _ = MeasureCacheAsync();
        if (_versionsRead)
            return;
        _versionsRead = true;
        _ = ReadVersionsAsync();
    }

    private async Task ReadVersionsAsync()
    {
        string? ffmpeg = await Task.Run(() => NativeTools.GetVersionAsync("ffmpeg")).ConfigureAwait(true);
        FfmpegVersion = ffmpeg ?? "not found";
        var mpv = (_editor.Player as MpvPlaybackEngine)?.Mpv;
        string? libmpv = mpv is null ? null : await Task.Run(() => mpv.GetPropertyString("mpv-version")).ConfigureAwait(true);
        LibmpvVersion = MpvVersion(libmpv) ?? "not available";
    }

    /// <summary>"mpv v0.39.0-dirty (…)" → "0.39.0-dirty".</summary>
    internal static string? MpvVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string v = text.Trim();
        if (v.StartsWith("mpv ", StringComparison.OrdinalIgnoreCase))
            v = v[4..].TrimStart();
        if (v.StartsWith('v'))
            v = v[1..];
        int space = v.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? v[..space] : v;
    }

    private static string ReadAppVersion() => Services.AppVersion.Text is { Length: > 0 } version ? version : "—";
}
