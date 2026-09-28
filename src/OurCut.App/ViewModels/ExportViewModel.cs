using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OurCut.App.Services;
using OurCut.Core.Time;
using OurCut.Media.Export;
using OurCut.Media.Probing;
using OurCut.Media.Tools;

namespace OurCut.App.ViewModels;

public enum ExportMode
{
    Copy,
    Smart,
    Encode,
}

public sealed partial class ExportModeOption(ExportViewModel owner, ExportMode mode, string title, string tag, string description, bool isAvailable,
    string cardTitle = "", string cardDescription = "")
    : ViewModelBase
{
    /// <summary>Title and one-line description on the mode card of the export dialog.</summary>
    public string CardTitle { get; } = cardTitle;
    public string CardDescription { get; } = cardDescription;

    public ExportMode Mode { get; } = mode;
    public string Title { get; } = title;
    public string Tag { get; } = tag;
    public string Description { get; } = description;

    /// <summary>Smart cut is shown but not available in Phase 1.</summary>
    public bool IsAvailable { get; } = isAvailable;

    public bool IsSelected => owner.Mode == Mode;

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));

    [RelayCommand]
    private void Pick()
    {
        if (IsAvailable)
            owner.Mode = Mode;
    }
}

public sealed partial class ChoiceOption(string label, Action pick) : ViewModelBase
{
    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Pick() => pick();
}

/// <summary>An audio setting for re-encoding, with a label that names the source codec for "copy".</summary>
public sealed record AudioChoice(string Label, AudioEncoding Encoding);

public enum ExportRowState
{
    Queued,
    Active,
    Done,
}

/// <summary>One step of a running export: a clip, or the final merge.</summary>
public sealed partial class ExportRowViewModel(string name) : ViewModelBase
{
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsDone), nameof(IsActive), nameof(IsQueued))]
    public partial ExportRowState State { get; set; }

    /// <summary>Progress of this step, 0..1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial double Progress { get; set; }

    /// <summary>The step shown large in the middle of the list.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>Finished and above the current step.</summary>
    [ObservableProperty]
    public partial bool IsPast { get; set; }

    public bool IsDone => State == ExportRowState.Done;
    public bool IsActive => State == ExportRowState.Active;
    public bool IsQueued => State == ExportRowState.Queued;

    public string StatusText => State switch
    {
        ExportRowState.Done => "done",
        ExportRowState.Active => Math.Round(Progress * 100).ToString(CultureInfo.InvariantCulture) + "%",
        _ => "queued",
    };
}

/// <summary>
/// Export dialog: settings, then the list of steps with progress. For a real file the export runs
/// ffmpeg through <see cref="ExportRunner"/> off the UI thread; with the design's sample (demo
/// mode) the progress is simulated.
/// </summary>
public sealed partial class ExportViewModel : ViewModelBase
{
    private static readonly string[] ContainerNames = ["MP4", "MOV", "MKV"];
    private const string DemoFolder = @"C:\Users\You\Videos\Exports";

    private readonly EditorViewModel _editor;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _statsTimer;
    private DateTime? _loopHoldStart;
    private CancellationTokenSource? _exportCts;
    private ExportPlan? _plan;
    private (ExportMode, string, bool, string, bool, bool, VideoEncoding, AudioChoice)? _beforeClaude;
    private List<(int Step, double From, double To)> _rowWindows = [];
    private IReadOnlyList<string> _written = [];
    private bool _overwrite;
    private DateTime _started;
    private DateTime? _finished;

    public ExportViewModel(EditorViewModel editor)
    {
        _editor = editor;
        Modes =
        [
            new(this, ExportMode.Copy, "Lossless copy", "Fastest", "Stream copy, no re-encoding. Cut points snap to the nearest keyframe.", true,
                "Lossless", "Stream copy · keyframe-snapped"),
            new(this, ExportMode.Smart, "Smart cut", "Soon", "Re-encodes only the frames around each cut and copies everything else.", false),
            new(this, ExportMode.Encode, "Re-encode", "Slowest", "Full transcode with the codec and quality you choose.", true,
                "Re-encode", "Frame-accurate · slower"),
        ];
        Containers = [.. ContainerNames.Select(f => new ChoiceOption(f, () => Container = f))];
        Outputs =
        [
            new("Merge into one file", () => Merge = true),
            new("Separate files", () => Merge = false),
        ];
        AudioChoices = [new("Copy (AAC 48 kHz)", AudioEncoding.Copy), new(AudioEncoding.Aac192.Label, AudioEncoding.Aac192)];
        Audio = AudioChoices[0];
        RefreshChoices();
    }

    public IReadOnlyList<ExportModeOption> Modes { get; }

    /// <summary>The modes offered in the dialog (smart cut is not available yet, so it is left out).</summary>
    public IReadOnlyList<ExportModeOption> ModeCards => [.. Modes.Where(m => m.IsAvailable)];

    /// <summary>Container, chapters and track options, folded away by default.</summary>
    [ObservableProperty]
    public partial bool ShowMoreOptions { get; set; }
    public IReadOnlyList<ChoiceOption> Containers { get; }
    public IReadOnlyList<ChoiceOption> Outputs { get; }
    public ObservableCollection<ExportRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<AudioChoice> AudioChoices { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDialogOpen), nameof(IsConfiguring), nameof(IsExporting), nameof(IsInProgress), nameof(ShowExportButton))]
    public partial ExportStage Stage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEncode), nameof(IsCopy), nameof(ModeTitle), nameof(Estimate), nameof(EstimateLine), nameof(Footer), nameof(Stats))]
    public partial ExportMode Mode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath), nameof(Footer), nameof(FileNamesText))]
    public partial string Container { get; set; } = "MP4";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath), nameof(FileCountText), nameof(Footer), nameof(CanAddChapters), nameof(FileNamesText))]
    public partial bool Merge { get; set; } = true;

    [ObservableProperty]
    public partial bool AddChapters { get; set; } = true;

    [ObservableProperty]
    public partial bool KeepAllTracks { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Estimate), nameof(EstimateLine))]
    public partial VideoEncoding Video { get; set; } = VideoEncoding.H264Quality;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Estimate))]
    public partial AudioChoice Audio { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath))]
    public partial string OutputFolder { get; set; } = DemoFolder;

    /// <summary>Overall progress 0..1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText), nameof(IsDone), nameof(IsNotDone), nameof(IsRunning), nameof(Title), nameof(Stats),
        nameof(ProgressText), nameof(ProgressDetail), nameof(IsInProgress))]
    public partial double Progress { get; set; }

    /// <summary>Why the export failed; null while it runs or after it succeeds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsRunning), nameof(Title), nameof(Stats), nameof(ProgressText), nameof(ProgressDetail),
        nameof(IsInProgress))]
    public partial string? ErrorText { get; set; }

    /// <summary>Waiting for the keyframe scan before the cuts can be planned.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Stats), nameof(ProgressText))]
    public partial bool IsPreparing { get; set; }

    /// <summary>Demo only: restart the simulated progress after it completes.</summary>
    public bool Loop { get; set; }

    /// <summary>Claude started the running (or latest) export.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressDetail))]
    public partial bool IsByClaude { get; private set; }

    /// <summary>The export runs with the dialog out of sight; the title bar's pill shows its progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDialogOpen))]
    public partial bool IsHidden { get; private set; }

    /// <summary>The design's sample has no file behind it, so its export is simulated.</summary>
    public bool IsSimulated => _editor.IsDemo || Preview is null;

    private MediaPreview? Preview => _editor.Media as MediaPreview;

    public bool IsDialogOpen => Stage != ExportStage.Closed && !IsHidden;
    public bool IsConfiguring => Stage == ExportStage.Configure;
    public bool IsExporting => Stage == ExportStage.Running;
    public bool IsEncode => Mode == ExportMode.Encode;
    public bool CanAddChapters => Merge;
    public bool IsDone => Progress >= 1;
    public bool IsNotDone => !IsDone;
    public bool HasError => ErrorText is not null;
    public bool IsRunning => !IsDone && !HasError;

    /// <summary>An export is running, shown or not.</summary>
    public bool IsInProgress => IsExporting && IsRunning;

    public string ModeTitle => Modes.First(m => m.Mode == Mode).Title;

    /// <summary>"Lossless" or "Re-encode", as on the mode card.</summary>
    public string ModeCardTitle => Modes.First(m => m.Mode == Mode).CardTitle;
    public string Extension => Container.ToLowerInvariant();
    public string BaseName => SafeFileName(_editor.ProjectName);

    /// <summary>The merged file's name before the planner makes it unique: "keynote-cut.mp4".</summary>
    private string MergedFileName => FileName(1, "", merged: true);

    /// <summary>Settings → Export → File names.</summary>
    private string Pattern => string.IsNullOrWhiteSpace(Defaults.FileNamePattern) ? ExportFileNames.DefaultPattern : Defaults.FileNamePattern;

    /// <summary>The pattern's {date}: today, or the design's day for its sample.</summary>
    private DateOnly Today => _editor.IsDemo ? new DateOnly(2026, 9, 25) : DateOnly.FromDateTime(DateTime.Today);

    /// <summary>An output's name from the pattern, before the planner makes it unique.</summary>
    private string FileName(int number, string label, bool merged) =>
        ExportFileNames.Fill(Pattern, BaseName, number, label, Today, merged) + "." + Extension;

    /// <summary>Separate files: the pattern with the project filled in, "keynote-cut-{n}.mp4".</summary>
    private string SeparateFilesPattern => Pattern.Replace("{project}", BaseName, StringComparison.Ordinal) + "." + Extension;

    public string OutputPath
    {
        get
        {
            if (_plan is not null && !IsSimulated)
                return Merge ? _plan.Outputs[0] : Path.Join(OutputFolder, SeparateFilesPattern);
            string path = Merge
                ? Path.Join(OutputFolder, MergedFileName)
                : Path.Join(OutputFolder, SeparateFilesPattern);
            // The design shows a Windows path.
            return IsSimulated ? path.Replace('/', '\\') : path;
        }
    }

    public string Summary => $"{Included.Count} clips · {TimeFormat.Duration(Total)} · from {_editor.MediaFileName}";

    /// <summary>Next to the dialog title: "4 clips · 00:04:31.360".</summary>
    public string HeaderSummary => $"{Included.Count} clips · {TimeFormat.Timecode(Total)}";

    public bool IsCopy => Mode == ExportMode.Copy;

    /// <summary>"Separate files (4)".</summary>
    public string SeparateFilesText => $"Separate files ({Included.Count})";

    /// <summary>The file names the export will write.</summary>
    public string FileNamesText
    {
        get
        {
            if (Merge)
                return MergedFileName;
            var on = Included;
            return on.Count switch
            {
                0 => "",
                1 => FileName(1, on[0].Label, merged: false),
                _ => $"{FileName(1, on[0].Label, merged: false)} … {FileName(on.Count, on[^1].Label, merged: false)}",
            };
        }
    }

    /// <summary>
    /// Lossless mode: how far the in-points move back to a keyframe. Out-points are exact.
    /// </summary>
    public string SnapNote
    {
        get
        {
            var keyframes = _editor.Media?.Keyframes ?? [];
            var on = Included;
            string note;
            if (keyframes.Count == 0)
            {
                note = "In points snap back to the previous keyframe. Out points stay exact.";
            }
            else
            {
                var shifts = on.Select(c => c.Start - (keyframes.LastOrDefault(k => k <= c.Start + 1e-6)))
                    .Where(d => d > 0.0005).ToList();
                note = shifts.Count == 0
                    ? "All in points already sit on keyframes."
                    : $"{shifts.Count} of {on.Count} in points will snap back to the previous keyframe (up to {shifts.Max().ToString("0.000", CultureInfo.InvariantCulture)} s earlier). Out points stay exact.";
            }
            return HasVolumeChanges ? note + " Audio tracks with a changed volume are re-encoded (AAC 192 kb/s); the video is still copied." : note;
        }
    }

    /// <summary>An audio track the export keeps plays at another volume than 0 dB.</summary>
    private bool HasVolumeChanges => _editor.AudioLanes.Any(l => l.GainDb != 0 && (KeepAllTracks || !l.IsMuted));

    /// <summary>The line above the progress bar: "Writing clip 2 of 4", "Concatenating 4 clips", "Export complete".</summary>
    public string ProgressText
    {
        get
        {
            if (HasError)
                return "Export failed";
            if (IsDone)
                return "Export complete";
            if (IsPreparing)
                return "Preparing…";
            int clips = Math.Max(1, Merge && Rows.Count > Included.Count ? Rows.Count - 1 : Rows.Count);
            int current = Rows.IndexOf(Rows.FirstOrDefault(r => r.IsCurrent) ?? Rows.LastOrDefault()!) + 1;
            return current > clips ? $"Concatenating {clips} clips" : $"Writing clip {Math.Max(1, current)} of {clips}";
        }
    }

    /// <summary>Under the progress bar: time left and speed, or where the result went.</summary>
    public string ProgressDetail => (IsByClaude ? "Started by Claude · " : "")
        + (HasError ? ErrorText! : IsDone ? $"{(Merge ? MergedFileName : FileCountText)} · {OutputFolder}" : Stats);

    /// <summary>The dialog's footer: estimated size and whether anything is re-encoded, and on which GPU.</summary>
    public string EstimateLine => Estimate + (Mode == ExportMode.Copy ? " · no re-encode"
        : " · " + Video.Label.Split(' ')[0] + " re-encode" + (GpuEncoderFor(Video) is { } gpu ? " on " + gpu.Name : ""));

    /// <summary>The GPU encoder found at start (Settings → Export); null for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Estimate), nameof(EstimateLine))]
    public partial GpuEncoderSupport? Gpu { get; set; }

    /// <summary>The GPU encoder a re-encode uses: when Settings → Export allows it and it can encode <paramref name="video"/>.</summary>
    private GpuEncoder? GpuEncoderFor(VideoEncoding video) =>
        Mode == ExportMode.Encode && Defaults.UseGpuEncoder ? Gpu?.For(video) : null;
    public string FileCountText => Merge ? "1 file" : $"{Included.Count} files";
    public string Footer => $"{ModeTitle} · {Container} · {(Merge ? "merged" : "separate files")}";
    public string PercentText => Math.Floor(Progress * 100).ToString(CultureInfo.InvariantCulture) + "%";
    public string Title => HasError ? "Export failed" : IsDone ? "Export complete" : IsPreparing ? "Preparing…" : "Exporting…";

    public string Estimate => Preview is { } p && !_editor.IsDemo
        ? $"≈ {Size(EstimatedBytes(p.Info))} · about {Secs(Total / EstimatedSpeed(p.Info))}"
        : $"≈ {SizeGb.ToString("0.00", CultureInfo.InvariantCulture)} GB · about {Secs(Total / Speed)}";

    public string Stats
    {
        get
        {
            if (IsSimulated)
            {
                double t = Total / Speed;
                string left = IsDone ? "done" : "~" + TimeFormat.Clock((1 - Progress) * t) + " left";
                return $"elapsed {TimeFormat.Clock(Progress * t)} · {left} · {Speed.ToString(CultureInfo.InvariantCulture)}× realtime";
            }
            if (HasError)
                return ErrorText!;
            if (IsPreparing)
                return "scanning keyframes";
            double elapsed = ((_finished ?? DateTime.UtcNow) - _started).TotalSeconds;
            string remaining = IsDone ? "done"
                : Progress > 0.02 ? "~" + TimeFormat.Clock(elapsed * (1 - Progress) / Progress) + " left"
                : "estimating";
            double speed = elapsed > 0.5 && _plan is not null ? Progress * _plan.OutputDuration / elapsed : 0;
            string speedText = speed > 0 ? $" · {speed.ToString(speed >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture)}× realtime" : "";
            return $"elapsed {TimeFormat.Clock(elapsed)} · {remaining}{speedText}";
        }
    }

    /// <summary>Seconds left, or null before there is an estimate.</summary>
    public double? RemainingSeconds
    {
        get
        {
            if (IsDone)
                return 0;
            // The simulated tick runs from 0 to 1 in 9 s.
            if (IsSimulated)
                return (1 - Progress) * 9;
            double elapsed = ((_finished ?? DateTime.UtcNow) - _started).TotalSeconds;
            return Progress > 0.02 ? elapsed * (1 - Progress) / Progress : null;
        }
    }

    /// <summary>Size of the files written: "428 MB"; the estimate for the design's sample.</summary>
    public string OutputSizeText => IsSimulated
        ? Size(SizeGb * 1e9)
        : Size(OutputFiles.Where(File.Exists).Sum(f => (double)new FileInfo(f).Length));

    /// <summary>What an export for Claude would write, for the request banner and card.</summary>
    public ClaudeExportTarget ClaudeTarget() => new(
        Merge ? OutputPath : OutputFolder,
        // Not Path.GetFileName: the simulated path uses backslashes on every OS.
        Merge ? MergedFileName : $"{Included.Count} files",
        OutputFolder, ModeCardTitle, Container, Included.Count, Total, Merge);

    private List<ClipViewModel> Included => [.. _editor.Clips.Where(c => c.IsIncluded)];
    private double Total => _editor.OutputDuration;
    private double Speed => Mode switch { ExportMode.Copy => 62, ExportMode.Smart => 18, _ => 1.4 };
    private double SizeGb => Total * (Mode == ExportMode.Encode ? 14 : 22) / 8 / 1000;

    /// <summary>Lossless output is about the source bitrate; re-encoding at these CRFs usually lands below it.</summary>
    private double EstimatedBytes(MediaInfo info)
    {
        double bitrate = info.BitRate > 0 ? info.BitRate : info.Size * 8 / Math.Max(1, info.Duration);
        double factor = Mode != ExportMode.Encode ? 1 : Video == VideoEncoding.H264Fast ? 0.5 : Video == VideoEncoding.H265 ? 0.4 : 0.7;
        return bitrate * factor * Total / 8;
    }

    /// <summary>Rough speed in × realtime: stream copy is disk bound; encoding depends on the pixel rate.</summary>
    private double EstimatedSpeed(MediaInfo info)
    {
        if (Mode != ExportMode.Encode || info.Video is not { } v)
            return 60;
        double pixelRate = Math.Max(1, (double)v.Width * v.Height * Math.Max(1, v.FrameRate));
        double hd = 1920.0 * 1080 * 30;
        double preset = Video == VideoEncoding.H264Fast ? 4 : Video == VideoEncoding.H265 ? 0.5 : 1.5;
        // A GPU encoder is several times faster than x264 or x265 on the CPU.
        double gpu = GpuEncoderFor(Video) is null ? 1 : Video == VideoEncoding.H265 ? 10 : 4;
        return Math.Max(0.05, preset * gpu * hd / pixelRate);
    }

    private static string Size(double bytes) => bytes >= 1e9
        ? (bytes / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + " GB"
        : Math.Max(1, Math.Round(bytes / 1e6)).ToString(CultureInfo.InvariantCulture) + " MB";

    /// <summary>"8 s", "3 min" (for "… left").</summary>
    internal static string ShortTime(double seconds) => Secs(seconds);

    private static string Secs(double x) => x < 60
        ? Math.Max(1, Math.Ceiling(x)).ToString(CultureInfo.InvariantCulture) + " s"
        : Math.Round(x / 60).ToString(CultureInfo.InvariantCulture) + " min";

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        string safe = new string([.. name.Select(ch => invalid.Contains(ch) ? '-' : ch)]).Trim().TrimEnd('.');
        return safe.Length == 0 ? "export" : safe;
    }

    partial void OnModeChanged(ExportMode value) => RefreshChoices();
    partial void OnOutputFolderChanged(string value) => ExistingFiles = [];
    partial void OnContainerChanged(string value) => RefreshChoices();
    partial void OnMergeChanged(bool value) => RefreshChoices();

    partial void OnProgressChanged(double value)
    {
        if (IsSimulated)
        {
            UpdateRows();
            if (value >= 1 && !Loop && Outcome == ExportOutcome.Running)
                Outcome = ExportOutcome.Done;
        }
    }

    private void RefreshChoices()
    {
        ExistingFiles = [];
        foreach (var m in Modes)
            m.Refresh();
        foreach (var c in Containers)
            c.IsSelected = c.Label == Container;
        Outputs[0].IsSelected = Merge;
        Outputs[1].IsSelected = !Merge;
        OnPropertyChanged(nameof(Estimate));
        OnPropertyChanged(nameof(EstimateLine));
    }

    [RelayCommand]
    public void Open()
    {
        if (!_editor.HasFile)
            return;
        // A hidden export of the user's that failed comes back with its error.
        if (IsInProgress || IsHidden && HasError && !IsByClaude)
        {
            IsHidden = false;
            return;
        }
        Configure();
        IsHidden = false;
    }

    /// <summary>Back to the settings, starting from Settings → Export for a new file.</summary>
    private void Configure()
    {
        ApplyDefaults(Preview is { } p && !_editor.IsDemo ? p.Info : null);
        _plan = null;
        ErrorText = null;
        Stage = ExportStage.Configure;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HeaderSummary));
        OnPropertyChanged(nameof(SeparateFilesText));
        OnPropertyChanged(nameof(FileNamesText));
        OnPropertyChanged(nameof(SnapNote));
        OnPropertyChanged(nameof(Estimate));
        OnPropertyChanged(nameof(EstimateLine));
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(OutputPath));
    }

    /// <summary>Hides the dialog; the export goes on.</summary>
    [RelayCommand]
    public void Hide() => IsHidden = true;

    /// <summary>The dialog's ✕ and Escape: hide a running export, otherwise close.</summary>
    [RelayCommand]
    public void Dismiss()
    {
        if (IsInProgress)
            Hide();
        else
            Close();
    }

    [RelayCommand]
    public void Close()
    {
        StopTimer();
        StopStatsTimer();
        StopRunning();
        ExistingFiles = [];
        Stage = ExportStage.Closed;
        Progress = 0;
        ErrorText = null;
        IsPreparing = false;
    }

    [RelayCommand]
    private async Task Browse()
    {
        if (_editor.Dialogs is null)
            return;
        string? folder = await _editor.Dialogs.PickFolderAsync("Save exports to", OutputFolder).ConfigureAwait(true);
        if (folder is not null)
            OutputFolder = folder;
    }

    [RelayCommand]
    private void ToggleMoreOptions() => ShowMoreOptions = !ShowMoreOptions;

    /// <summary>Cancel export: stops it and goes back to the settings.</summary>
    [RelayCommand]
    public void CancelExport()
    {
        StopTimer();
        StopStatsTimer();
        StopRunning();
        // A hidden export has no settings on screen to go back to.
        Stage = IsHidden ? ExportStage.Closed : ExportStage.Configure;
        Progress = 0;
        ErrorText = null;
        IsPreparing = false;
        Rows.Clear();
    }

    /// <summary>Cancels a running export; it counts as cancelled at once, before ffmpeg has stopped.</summary>
    private void StopRunning()
    {
        _exportCts?.Cancel();
        if (Outcome == ExportOutcome.Running)
            Outcome = ExportOutcome.Cancelled;
    }

    [RelayCommand]
    private void Reveal()
    {
        if (!IsSimulated)
            FileManager.Reveal(_written.Count > 0 ? _written[0] : OutputFolder);
    }

    [RelayCommand]
    public async Task StartAsync()
    {
        IsByClaude = false;
        IsHidden = false;
        Loop = false;
        if (IsSimulated)
        {
            Start(0);
            return;
        }
        _overwrite = Defaults.IfExists == FileExistsAction.Overwrite;
        // Settings → Export → If the file exists: Ask. The dialog asks before anything is written.
        if (Defaults.IfExists == FileExistsAction.Ask
            && ExportPlanner.OutputNames(_editor.Session.Project, BuildSettings()).Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } existing)
        {
            ExistingFiles = existing;
            return;
        }
        await RunAsync(Preview!).ConfigureAwait(true);
    }

    /// <summary>Files the export would replace, while the dialog asks what to do about them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAskingAboutExisting), nameof(ExistingFilesText), nameof(ShowExportButton))]
    public partial IReadOnlyList<string> ExistingFiles { get; private set; } = [];

    public bool IsAskingAboutExisting => ExistingFiles.Count > 0;

    /// <summary>The footer's question: "keynote-cut.mp4 already exists." or "3 of the files already exist."</summary>
    public string ExistingFilesText => ExistingFiles.Count switch
    {
        0 => "",
        1 => $"{Path.GetFileName(ExistingFiles[0])} already exists.",
        var n => $"{n} of the files already exist.",
    };

    public bool ShowExportButton => IsConfiguring && !IsAskingAboutExisting;

    /// <summary>Answer to <see cref="ExistingFiles"/>: replace them.</summary>
    [RelayCommand]
    private Task Overwrite() => StartAfterAskingAsync(overwrite: true);

    /// <summary>Answer to <see cref="ExistingFiles"/>: " (2)" is added to the new names.</summary>
    [RelayCommand]
    private Task AddNumber() => StartAfterAskingAsync(overwrite: false);

    private async Task StartAfterAskingAsync(bool overwrite)
    {
        if (!IsAskingAboutExisting || Preview is not { } preview)
            return;
        ExistingFiles = [];
        _overwrite = overwrite;
        await RunAsync(preview).ConfigureAwait(true);
    }

    /// <summary>Starts the simulated export at <paramref name="progress"/> (demo screens); Claude's runs hidden.</summary>
    public void Start(double progress, bool byClaude = false)
    {
        IsByClaude = byClaude;
        IsHidden = byClaude;
        ErrorText = null;
        if (byClaude)
            Loop = false;
        BuildRows();
        Stage = ExportStage.Running;
        Progress = progress;
        UpdateRows();
        StopTimer();
        _loopHoldStart = null;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
        Outcome = ExportOutcome.Running;
    }

    /// <summary>The latest export: running, or how it ended.</summary>
    [ObservableProperty]
    public partial ExportOutcome Outcome { get; private set; }

    /// <summary>The files the running export writes, or the latest one wrote.</summary>
    public IReadOnlyList<string> OutputFiles => _written.Count > 0 ? _written : _plan?.Outputs ?? [];

    /// <summary>Applies Claude's choices without showing the dialog. Returns why it cannot export, or null.</summary>
    public string? PrepareForClaude(ExportMode? mode = null, string? container = null, bool? merge = null, string? folder = null,
        bool? chapters = null, bool? keepAllTracks = null, VideoEncoding? video = null, bool? copyAudio = null)
    {
        if (!_editor.HasFile)
            return "No video is open.";
        if (IsSimulated)
            return "Exporting is not available for this file.";
        if (Outcome == ExportOutcome.Running)
            return "An export is already running; wait for it (get_export_status) or cancel it.";
        if (Included.Count == 0)
            return "There are no included clips to export.";
        if (IsDialogOpen)
            return "The user has the Export dialog open. Ask them to export from there, or to close it first.";
        Configure();
        IsHidden = true;
        _beforeClaude = (Mode, Container, Merge, OutputFolder, AddChapters, KeepAllTracks, Video, Audio);
        if (mode is { } m)
            Mode = m;
        if (container is not null)
            Container = container;
        if (merge is { } mg)
            Merge = mg;
        if (folder is not null)
            OutputFolder = folder;
        if (chapters is { } ch)
            AddChapters = ch;
        if (keepAllTracks is { } all)
            KeepAllTracks = all;
        if (video is not null)
            Video = video;
        if (copyAudio is { } copy)
            Audio = copy ? AudioChoices[0] : AudioChoices[^1];
        return null;
    }

    /// <summary>Claude's export did not start (denied, withdrawn, refused): the dialog's own choices come back.</summary>
    public void AbandonPreparedForClaude()
    {
        var before = _beforeClaude;
        _beforeClaude = null;
        // The user opened the dialog meanwhile; what it shows is theirs now.
        if (before is not { } b || !IsHidden || Stage != ExportStage.Configure)
            return;
        (Mode, Container, Merge, OutputFolder, AddChapters, KeepAllTracks, Video, Audio) = b;
        Close();
    }

    /// <summary>Runs the prepared export for Claude with the dialog hidden.</summary>
    public string? StartPreparedForClaude()
    {
        if (Outcome == ExportOutcome.Running)
            return "An export is already running; wait for it (get_export_status) or cancel it.";
        if (Included.Count == 0)
            return "There are no included clips to export.";
        _beforeClaude = null;
        IsByClaude = true;
        IsHidden = true;
        Loop = false;
        _ = RunAsync(Preview!);
        return null;
    }

    /// <summary>Retry on Claude's failed card: the same settings again (simulated for the design's sample).</summary>
    public string? RetryForClaude()
    {
        if (!_editor.HasFile)
            return "No video is open.";
        if (Outcome == ExportOutcome.Running)
            return "An export is already running.";
        if (IsSimulated)
        {
            Start(0, byClaude: true);
            return null;
        }
        Configure();
        IsHidden = true;
        return StartPreparedForClaude();
    }

    /// <summary>The settings as the Media layer takes them.</summary>
    public ExportSettings BuildSettings()
    {
        var tracks = _editor.Session.Project.Source?.AudioTracks ?? [];
        return new ExportSettings
        {
            Mode = Mode switch { ExportMode.Copy => CutMode.Lossless, ExportMode.Smart => CutMode.SmartCut, _ => CutMode.Reencode },
            Container = Container switch { "MOV" => OutputContainer.Mov, "MKV" => OutputContainer.Mkv, _ => OutputContainer.Mp4 },
            Merge = Merge,
            AddChapters = AddChapters && Merge,
            KeepAllTracks = KeepAllTracks,
            // Muted lanes are left out unless every track is kept.
            AudioStreamIndexes = [.. _editor.AudioLanes.Where(l => !l.IsMuted && l.Stream < tracks.Length).Select(l => tracks[l.Stream].Index)],
            AudioGainsDb = _editor.Session.Project.AudioMix.Where(m => m.GainDb != 0).ToDictionary(m => m.Index, m => m.GainDb),
            OutputFolder = OutputFolder,
            BaseName = BaseName,
            FileNamePattern = Pattern,
            Date = Today,
            // Claude's exports never replace a file.
            Overwrite = _overwrite && !IsByClaude,
            Video = Video,
            GpuEncoder = GpuEncoderFor(Video),
            Audio = Audio.Encoding,
        };
    }

    private async Task RunAsync(MediaPreview preview)
    {
        var cts = new CancellationTokenSource();
        _exportCts = cts;
        _plan = null;
        _written = [];
        _finished = null;
        ErrorText = null;
        Rows.Clear();
        Progress = 0;
        Stage = ExportStage.Running;
        Outcome = ExportOutcome.Running;
        try
        {
            IsPreparing = Mode == ExportMode.Copy && !preview.KeyframesTask.IsCompleted;
            var keyframes = Mode == ExportMode.Copy
                ? await preview.KeyframesTask.WaitAsync(cts.Token).ConfigureAwait(true)
                : preview.Keyframes;
            cts.Token.ThrowIfCancellationRequested();
            IsPreparing = false;

            var plan = ExportPlanner.Plan(_editor.Session.Project, preview.Info, keyframes, BuildSettings());
            _plan = plan;
            BuildRows(plan);
            OnPropertyChanged(nameof(OutputPath));
            _started = DateTime.UtcNow;
            StartStatsTimer();

            var progress = new Progress<ExportProgress>(p =>
            {
                if (ReferenceEquals(_exportCts, cts) && ErrorText is null && _finished is null)
                    ApplyProgress(p);
            });
            var written = await Task.Run(() => ExportRunner.RunAsync(plan, progress, cts.Token), cts.Token).ConfigureAwait(true);
            // Cancelled and replaced by a newer export: the state is the newer one's, here and below.
            if (!ReferenceEquals(_exportCts, cts))
                return;
            _written = written;
            _finished = DateTime.UtcNow;
            ApplyProgress(new ExportProgress(plan.Steps.Count - 1, 1, 1));
            Outcome = ExportOutcome.Done;
            // Claude's card has its own Show in folder; the user's export shows its file (Settings → Export → After export).
            if (!IsByClaude && Defaults.AfterExport == AfterExportAction.ShowInFolder && _written.Count > 0)
                _editor.RevealInFolder?.Invoke(_written[0]);
            _editor.ShowMessage(_written.Count == 1 ? $"Exported {Path.GetFileName(_written[0])}" : $"Exported {_written.Count} files");
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(_exportCts, cts))
                return;
            Outcome = ExportOutcome.Cancelled;
            if (Stage == ExportStage.Running)
                Close();
            _editor.ShowMessage("Export cancelled.");
        }
        catch (Exception e) when (e is MediaToolException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            if (!ReferenceEquals(_exportCts, cts))
                return;
            _finished = DateTime.UtcNow;
            IsPreparing = false;
            ErrorText = e.Message;
            Outcome = ExportOutcome.Failed;
            if (IsHidden && !IsByClaude)
                _editor.ShowMessage("Export failed: " + e.Message);
        }
        finally
        {
            if (ReferenceEquals(_exportCts, cts))
            {
                StopStatsTimer();
                OnPropertyChanged(nameof(Stats));
                _exportCts = null;
            }
            cts.Dispose();
        }
    }

    private void StartStatsTimer()
    {
        StopStatsTimer();
        _statsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) =>
            {
                OnPropertyChanged(nameof(RemainingSeconds));
                OnPropertyChanged(nameof(Stats));
                OnPropertyChanged(nameof(ProgressDetail));
            });
        _statsTimer.Start();
    }

    private void StopStatsTimer()
    {
        _statsTimer?.Stop();
        _statsTimer = null;
    }

    private void Tick()
    {
        if (Stage != ExportStage.Running)
            return;
        double p = Progress + 0.1 / 9;
        if (p >= 1)
        {
            p = 1;
            if (Loop)
            {
                _loopHoldStart ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _loopHoldStart > TimeSpan.FromSeconds(3))
                {
                    _loopHoldStart = null;
                    p = 0.05;
                }
            }
            else
            {
                StopTimer();
            }
        }
        Progress = p;
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>Rows for the simulated export, as the design lists them.</summary>
    private void BuildRows()
    {
        Rows.Clear();
        var on = Included;
        for (int i = 0; i < on.Count; i++)
        {
            string name = Merge
                ? $"{i + 1} · {on[i].Label}"
                : FileName(i + 1, on[i].Label, merged: false);
            Rows.Add(new ExportRowViewModel(name));
        }
        if (Merge)
            Rows.Add(new ExportRowViewModel($"Merge into {MergedFileName}"));
    }

    /// <summary>
    /// One row per step of the plan. A re-encoded merge is a single ffmpeg pass, so it is shown as one
    /// row per clip, each covering its share of that pass.
    /// </summary>
    private void BuildRows(ExportPlan plan)
    {
        Rows.Clear();
        var windows = new List<(int, double, double)>();
        if (plan.Steps is [{ Kind: ExportStepKind.EncodeMerged } merged])
        {
            double total = merged.Duration, at = 0;
            foreach (var clip in plan.Clips)
            {
                Rows.Add(new ExportRowViewModel($"{clip.Number} · {clip.Label}"));
                double to = total > 0 ? at + clip.OutputDuration / total : 1;
                windows.Add((0, at, to));
                at = to;
            }
        }
        else
        {
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                Rows.Add(new ExportRowViewModel(plan.Steps[i].Name));
                windows.Add((i, 0, 1));
            }
        }
        _rowWindows = windows;
        SetCurrentRow();
    }

    private void ApplyProgress(ExportProgress p)
    {
        for (int i = 0; i < Rows.Count && i < _rowWindows.Count; i++)
        {
            var (step, from, to) = _rowWindows[i];
            double f = step < p.StepIndex ? 1
                : step > p.StepIndex ? 0
                : to > from ? Math.Clamp((p.StepFraction - from) / (to - from), 0, 1) : 1;
            bool started = step < p.StepIndex || (step == p.StepIndex && p.StepFraction >= from);
            Rows[i].Progress = f;
            Rows[i].State = f >= 1 ? ExportRowState.Done : started ? ExportRowState.Active : ExportRowState.Queued;
        }
        Progress = Math.Clamp(p.Overall, 0, 1);
        SetCurrentRow();
    }

    /// <summary>Splits overall progress into per-step progress, as the design does.</summary>
    private void UpdateRows()
    {
        if (Rows.Count == 0)
            return;
        var on = Included;
        double total = Total, w = Merge ? 0.92 : 1, acc = 0, p = Progress;
        for (int i = 0; i < on.Count && i < Rows.Count; i++)
        {
            double cw = total > 0 ? on[i].Duration / total * w : 0;
            Rows[i].Progress = cw > 0 ? Math.Clamp((p - acc) / cw, 0, 1) : 1;
            acc += cw;
        }
        if (Merge)
            Rows[^1].Progress = Math.Clamp((p - 0.92) / 0.08, 0, 1);

        foreach (var r in Rows)
            r.State = r.Progress >= 1 ? ExportRowState.Done : r.Progress > 0 ? ExportRowState.Active : ExportRowState.Queued;
        SetCurrentRow();
    }

    /// <summary>The first unfinished row is shown large; the ones above it are dimmed.</summary>
    private void SetCurrentRow()
    {
        int current = -1;
        for (int i = 0; i < Rows.Count; i++)
        {
            if (current < 0 && Rows[i].State != ExportRowState.Done)
                current = i;
        }
        if (current < 0)
            current = Rows.Count - 1;
        for (int i = 0; i < Rows.Count; i++)
        {
            Rows[i].IsCurrent = i == current;
            Rows[i].IsPast = i < current;
        }
        OnPropertyChanged(nameof(ProgressText));
    }
}

public enum ExportStage
{
    Closed,
    Configure,
    Running,
}

/// <summary>How the latest real export went (what Claude is told).</summary>
public enum ExportOutcome
{
    None,
    Running,
    Done,
    Failed,
    Cancelled,
}
