using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.App.Demo;
using HighlightCut.App.Services;
using HighlightCut.Core.Editing;
using HighlightCut.Core.Model;
using HighlightCut.Core.Serialization;
using HighlightCut.Core.Time;
using HighlightCut.Media.Playback;
using HighlightCut.Media.Tools;
using HighlightCut.Transcription;
using HighlightCut.Transcription.Models;

namespace HighlightCut.App.ViewModels;

/// <summary>
/// State of the editor window. The project itself lives in an <see cref="EditorSession"/> from
/// HighlightCut.Core: every edit is a Core command (undoable, and callable by a future MCP server), and
/// this view model mirrors the resulting project for the views. Playback position, selection and
/// zoom are view state and stay here.
/// </summary>
public sealed partial class EditorViewModel : ViewModelBase
{
    private static readonly double[] Speeds = [0.5, 1, 1.5, 2];
    private readonly Dictionary<HistoryEntry, ClaudeLogItemViewModel> _historyItems = [];
    private DispatcherTimer? _playTimer;
    private DispatcherTimer? _messageTimer;
    private DispatcherTimer? _autosaveTimer;
    private DateTime _lastTick;
    private CancellationTokenSource? _openCts;
    private IReadOnlyList<double>? _appliedKeyframes;
    private bool _previewErrorShown;
    private IPlayer? _player;
    private bool _playerLoaded;
    private bool _playerHasFile;
    private int _playerGeneration;
    private bool _timeFromPlayer;
    private string? _openError;
    private string? _openedPath;

    public EditorViewModel()
    {
        Export = new ExportViewModel(this);
        Claude = new ClaudePanelViewModel(new ClaudeExportViewModel(this), new ClaudeFileRequestViewModel(this));
        Settings = new SettingsViewModel(this);
        Tour = new WelcomeTourViewModel(this);
        WhatsNew = new WhatsNewViewModel(this);
        TranscriptPanel = CreateTranscriptPanel();
        // The Transcript chip and "Transcribe when a video is opened" are one choice.
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.TranscribeOnOpen) && !IsDemo)
                ShowTranscriptLane = Settings.TranscribeOnOpen;
        };
        // With "transcribe when opened" off, only a transcript someone asked for (or the tab's Download) is started;
        // one made earlier with the new model is shown.
        Settings.TranscriptionChanged += (_, _) =>
        {
            if ((Settings.TranscribeOnOpen || TranscriptPanel.TranscribeWhenInstalled || Media is { TranscriptState: not TranscriptState.None })
                && StartTranscription() is null)
                TranscriptPanel.TranscribeWhenInstalled = false;
            else
                LoadCachedTranscript();
        };
        Session.Changed += OnSessionChanged;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StatusRight) or nameof(MediaInfoText))
                OnPropertyChanged(nameof(StatusLeft));
        };
        Clips.CollectionChanged += OnClipsChanged;
        RecentFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentFiles));
        Claude.Changed += (_, _) => ApplyClaudeHighlights();
        Claude.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClaudePanelViewModel.IsBusy))
                OnPropertyChanged(nameof(IsClaudeBusy));
            if (e.PropertyName is nameof(ClaudePanelViewModel.McpText))
                OnPropertyChanged(nameof(McpText));
        };
        Export.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ExportViewModel.Stage) or nameof(ExportViewModel.Progress) or nameof(ExportViewModel.Mode)
                or nameof(ExportViewModel.ErrorText) or nameof(ExportViewModel.IsInProgress))
            {
                OnPropertyChanged(nameof(ExportButtonText));
                OnPropertyChanged(nameof(StatusRight));
            }
        };
    }

    /// <summary>The Core editing session: the project and its undo history.</summary>
    public EditorSession Session { get; } = new();

    public ClaudePanelViewModel Claude { get; }
    public ExportViewModel Export { get; }

    /// <summary>The settings dialog (Settings → Transcription).</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>The welcome tour: once on the first launch, then from the project menu.</summary>
    public WelcomeTourViewModel Tour { get; }

    /// <summary>What's new: once after an update, then from the project menu.</summary>
    public WhatsNewViewModel WhatsNew { get; }

    /// <summary>The screen over the editor while a file is prepared.</summary>
    public ProcessingViewModel Processing { get; } = new();
    public ObservableCollection<ClipViewModel> Clips { get; } = [];
    public ObservableCollection<AudioLaneViewModel> AudioLanes { get; } = [];
    public ObservableCollection<RecentFileViewModel> RecentFiles { get; } = [];
    public bool HasRecentFiles => RecentFiles.Count > 0;

    /// <summary>File dialogs, provided by the window.</summary>
    public IFileDialogs? Dialogs { get; set; }

    /// <summary>Shows a file in the system file manager (after an export); none in tests and the demo.</summary>
    public Action<string>? RevealInFolder { get; set; }

    /// <summary>Probes and analyses media files. Without one, videos cannot be opened.</summary>
    public IMediaOpener? MediaOpener { get; set; }

    /// <summary>
    /// Plays the video (libmpv). Null in tests and when libmpv is missing; playback is then simulated
    /// over the thumbnails, as it is for the design's sample.
    /// </summary>
    public IPlayer? Player
    {
        get => _player;
        set
        {
            if (ReferenceEquals(_player, value))
                return;
            if (_player is not null)
                _player.StateChanged -= OnPlayerStateChanged;
            _player = value;
            _playerLoaded = false;
            if (value is not null)
                value.StateChanged += OnPlayerStateChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPlayback));
        }
    }

    /// <summary>The loaded file plays through <see cref="Player"/> (not simulated).</summary>
    public bool HasPlayback => _player is not null && _playerLoaded && Media is { IsPlayable: true };

    /// <summary>Remembers recently opened files; null keeps no history (demo mode and tests).</summary>
    public RecentFilesStore? RecentStore { get; set; }

    /// <summary>Raised whenever anything drawn on the timeline changes.</summary>
    public event EventHandler? TimelineChanged;

    /// <summary>
    /// Demo mode shows the design's scripted Claude activity in the Claude panel. Otherwise the panel
    /// lists the project's edit history.
    /// </summary>
    public bool IsDemo { get; set; }

    /// <summary>
    /// Starts a new project's history in the Claude panel. Leaving the scripted demo also drops its
    /// sample recent files and its pretend MCP connection (demo runs have no MCP server).
    /// </summary>
    public void LeaveDemo()
    {
        if (IsDemo)
        {
            RecentFiles.Clear();
            // A demo run has no MCP server; its connection was the design's.
            Claude.IsConnected = false;
            Claude.IsListening = false;
            Claude.ConnectedSince = null;
            Claude.ClientName = null;
            Claude.OtherWindowProject = null;
        }
        bool wasDemo = IsDemo;
        IsDemo = false;
        Claude.Log.Clear();
        _historyItems.Clear();
        // The design's chips were for its screens; the user's own come back.
        if (wasDemo)
            Settings.ApplyTimeline();
    }

    // ---- File ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile), nameof(IsEmpty), nameof(Duration), nameof(DurationText),
        nameof(SourceLengthText), nameof(StatusRight), nameof(FrameText), nameof(HasSilenceData), nameof(HasSceneData),
        nameof(SilenceTip), nameof(ScenesTip), nameof(CanToggleScenes), nameof(ScenesOn), nameof(KeyframesTip), nameof(WaveformTip),
        nameof(SnapTip), nameof(CanToggleKeyframes), nameof(KeyframesOn), nameof(CanToggleAudio), nameof(WaveformOn), nameof(SilencesOn),
        nameof(TransportDurationText), nameof(VideoAspect), nameof(HasPlayback))]
    public partial IMediaPreview? Media { get; set; }

    /// <summary>Width / height of the picture, for the player frame (16:9 until a file is open).</summary>
    public double VideoAspect => (CurrentVideoPreview ?? Media)?.AspectRatio is > 0 and var ratio ? ratio : 16.0 / 9.0;

    partial void OnMediaChanged(IMediaPreview? oldValue, IMediaPreview? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.Changed -= OnPreviewChanged;
            // Each video's preview belongs to the editor (_previews); the timeline of several is only a view of them.
            (oldValue as TimelinePreview)?.Dispose();
        }
        if (newValue is not null)
            newValue.Changed += OnPreviewChanged;
        _previewErrorShown = false;
    }

    /// <summary>More thumbnails, waveform or keyframes arrived from the background analysis.</summary>
    private void OnPreviewChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, Media) || Media is not { } media)
            return;
        var keyframes = media.Keyframes;
        if (!ReferenceEquals(_appliedKeyframes, keyframes))
        {
            _appliedKeyframes = keyframes;
            Session.Keyframes = keyframes;
        }
        foreach (string name in (string[])[nameof(HasSilenceData), nameof(HasSceneData), nameof(SilenceTip), nameof(ScenesTip),
                     nameof(CanToggleScenes), nameof(ScenesOn), nameof(KeyframesTip), nameof(WaveformTip), nameof(SnapTip),
                     nameof(CanToggleKeyframes), nameof(KeyframesOn), nameof(CanToggleAudio), nameof(WaveformOn), nameof(SilencesOn)])
            OnPropertyChanged(name);
        Processing.Update();
        if (media.AnalysisError is { } error && !_previewErrorShown)
        {
            _previewErrorShown = true;
            ShowMessage("Could not analyse the whole file: " + error);
        }
        OnPropertyChanged(nameof(StatusRight));
        RaiseTimelineChanged();
    }

    /// <summary>The timeline ruler is drawn for this duration even before a file is open (demo only).</summary>
    [ObservableProperty]
    public partial double PlaceholderDuration { get; set; }

    [ObservableProperty]
    public partial string MediaFileName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MediaInfoText))]
    public partial string MediaInfo { get; set; } = "";

    /// <summary>Where the project is saved; null until it is saved once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusRight), nameof(WindowTitle))]
    public partial string? ProjectPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusRight))]
    public partial bool IsDirty { get; set; }

    /// <summary>Whether the last save happened automatically.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusRight))]
    public partial bool LastSaveWasAuto { get; set; }

    public string ProjectName => Session.Project.Name;
    public bool HasFile => Media is not null && Session.Project.Source is not null;
    public bool IsEmpty => !HasFile;
    public double Duration => HasFile ? Session.Project.TimelineDuration : PlaceholderDuration;
    public double FrameRate => (Session.Project.SourceAt(Time) ?? Session.Project.Source)?.FrameRate is > 0 and var fps ? fps : 30;
    public string DurationText => TimeFormat.Timecode(Duration);

    /// <summary>Duration next to the player's timecode; zero until a file is open.</summary>
    public string TransportDurationText => TimeFormat.Timecode(HasFile ? Duration : 0);
    public string SourceLengthText => HasFile ? TimeFormat.WholeSeconds(Duration) : "no media";
    public string WindowTitle => HasFile ? $"{ProjectName} — HighlightCut" : "HighlightCut";

    /// <summary>Project name in the title bar ("Project / interview_final_v3").</summary>
    public string ProjectTitle => HasFile ? ProjectName : "Untitled";

    /// <summary>Media details in the status bar, or "No file open".</summary>
    public string MediaInfoText => !HasFile ? "No file open"
        : HasSeveralVideos ? $"{Session.Project.Sources.Count} videos  ·  {TimeFormat.WholeSeconds(Duration)}  ·  {(MediaInfo.Length > 0 ? MediaInfo : MediaFileName)}"
        : MediaInfo.Length > 0 ? MediaInfo : MediaFileName;

    /// <summary>
    /// What draws the video, as the video view reports it: "OpenGL · (the GPU)", "software", "none: why", or null before
    /// a video is shown.
    /// </summary>
    [ObservableProperty]
    public partial string? VideoOutput { get; set; }

    // Without a picture the player shows only the timeline thumbnails, which would pass for a blurry video.
    partial void OnVideoOutputChanged(string? value)
    {
        if (value is not null && value.StartsWith("none: ", StringComparison.Ordinal))
            ShowMessage("No video picture (" + value["none: ".Length..] + "). The player shows thumbnails only.");
    }

    // ---- Playback ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText), nameof(FrameText), nameof(PlayheadText))]
    public partial double Time { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    public partial double Speed { get; set; } = 1;

    [ObservableProperty]
    public partial double Volume { get; set; } = 0.7;

    public string TimeText => TimeFormat.Timecode(Time);
    public string PlayheadText => TimeFormat.MinutesSeconds(Time);
    public string FrameText => $"{TimeFormat.Timecode(Time)} · f {Math.Round(Time * FrameRate).ToString(CultureInfo.InvariantCulture)}";
    public string SpeedText => Speed.ToString(CultureInfo.InvariantCulture) + "×";

    // ---- Selection -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExcludeLabel), nameof(HasSelection), nameof(SelectionInfo), nameof(CanJoinWithNext))]
    public partial ClipViewModel? SelectedClip { get; set; }

    public bool HasSelection => SelectedClip is not null;
    public ClipViewModel? CurrentClip => Clips.FirstOrDefault(c => c.Contains(Time));
    public bool IsInClip => CurrentClip is { IsIncluded: true };
    public bool IsInExcluded => HasFile && !IsInClip;
    public int CurrentClipNumber => CurrentClip?.Number ?? 0;
    public string CurrentClipLabel => CurrentClip?.Label ?? "";
    public bool IsCurrentClipAi => CurrentClip?.IsAi ?? false;
    public string ExcludeLabel => SelectedClip is { IsIncluded: false } ? "Keep" : "Exclude";

    // ---- Timeline view -------------------------------------------------------------------

    /// <summary>Zoom slider position 0..1; the timeline maps it to 1× … frame level.</summary>
    [ObservableProperty]
    public partial double ZoomLevel { get; set; }

    /// <summary>
    /// Keyframe snapping while trimming ("Snap to keyframes" in the timeline toolbar), once the keyframes are known (the
    /// Keyframes chip finds them). Alt turns it off during a drag.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusRight), nameof(SnapTip))]
    public partial bool SnapToKeyframes { get; set; } = true;

    /// <summary>Timeline tool: Select (V) selects and trims; Split cuts the clip where you click.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectTool), nameof(IsSplitTool))]
    public partial TimelineTool Tool { get; set; }

    public bool IsSelectTool => Tool == TimelineTool.Select;
    public bool IsSplitTool => Tool == TimelineTool.Split;

    // Marker layers on the timeline (toolbar chips). What a chip shows is worked out only while it is on. By default only
    // Waveform (and Snap) is: its audio is read in the background, so opening a video still reads nothing but its header
    // before it plays. They start on for the design's screens.

    /// <summary>Keyframe ticks; while on, keyframes are scanned in every video opened (trims then snap to them).</summary>
    [ObservableProperty]
    public partial bool ShowKeyframes { get; set; } = true;

    /// <summary>The audio waveform; while on (the default), the audio of every video opened is read in the background.</summary>
    [ObservableProperty]
    public partial bool ShowWaveform { get; set; } = true;

    /// <summary>Silence bands; while on, the audio of every video opened is read to find them.</summary>
    [ObservableProperty]
    public partial bool ShowSilences { get; set; } = true;

    /// <summary>Scene change markers; while on, scene changes are found in every video opened (Settings: off at first).</summary>
    [ObservableProperty]
    public partial bool ShowScenes { get; set; }

    /// <summary>
    /// Thumbnails on the video track; while on, they are made for every video opened (Settings: off at first, the player
    /// shows the picture anyway). Off, the track is a plain strip and no thumbnails are made.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowFrames { get; set; }

    /// <summary>Silences and scene changes found so far (the toolbar chips are off without any).</summary>
    public bool HasSilenceData => Media?.Silences.Count > 0;
    public bool HasSceneData => Media?.SceneChanges.Count > 0;

    public string SilenceTip => !ShowSilences ? "Show pauses of a second or more (finding them reads all the audio)"
        : Media is not { } media ? "Silence bands: pauses of a second or more, found in every video you open"
        : media.AudioStreamCount == 0 ? "No audio in this file"
        : media.Silences.Count is > 0 and var n ? $"Silence bands: {n} pause{(n == 1 ? "" : "s")} of a second or more"
            + (media.SilencesComplete ? "" : " so far")
        : !media.SilencesComplete ? "Reading the audio to find pauses…"
        : "No pauses of a second or more in this file";

    public string KeyframesTip => !ShowKeyframes
        ? "Show keyframes: the frames a clip can start on without re-encoding. Finding them reads the video once; trims then snap to them"
        : Media is not { } media ? "Keyframe ticks: found in every video you open; trims snap to them"
        : media.FrameRate <= 0 ? "No video in this file"
        : !media.KeyframesComplete ? "Finding keyframes…"
        : $"Keyframes: {media.Keyframes.Count}. A lossless export starts each clip on one; trims snap to them";

    public string WaveformTip => !ShowWaveform ? "Show the audio waveform: where it gets loud or goes quiet (the audio is read while the video plays)"
        : Media is not { } media ? "Audio waveform: read in every video you open"
        : media.AudioStreamCount == 0 ? "No audio in this file"
        : media.SilencesComplete ? "Audio waveform: how loud each track is over time"
        : "Reading the audio…";

    public string SnapTip => (HasFile && Media is { Keyframes.Count: 0 }
            ? "Snap trims to keyframes (turn on Keyframes to find them)."
            : "Snap trims to keyframes.")
        + " A trim always stops at the next clip; hold Alt to drag freely";

    public string ScenesTip => !ShowScenes ? "Show scene changes (finding them reads every frame, so it takes a while)"
        : Media is not { } media ? "Scene changes: found in every video you open"
        : media.SceneChanges.Count is > 0 and var n
            ? $"Scene changes: {n}" + (media.ScenesComplete ? "" : " so far")
        : !media.ScenesComplete ? "Detecting scene changes…"
        : "No scene changes found";

    /// <summary>The selected clip in the timeline toolbar: "Clip 3  00:04:22.080 → 00:06:05.520  ·  1:43.440".</summary>
    public string SelectionInfo => SelectedClip is { } c
        ? $"Clip {c.Number}  {TimeFormat.Timecode(c.Start)} → {TimeFormat.Timecode(c.End)}  ·  {c.ShortDurationText}"
        : "";

    [RelayCommand]
    private void SelectTool() => Tool = TimelineTool.Select;

    [RelayCommand]
    private void SplitTool() => Tool = TimelineTool.Split;

    [RelayCommand]
    private void ToggleKeyframes() => ShowKeyframes = !ShowKeyframes;

    [RelayCommand]
    private void ToggleWaveform() => ShowWaveform = !ShowWaveform;

    [RelayCommand]
    private void ToggleSilences() => ShowSilences = !ShowSilences;

    /// <summary>The Keyframes chip works without a file (the choice is kept for the next one), not for a file without video.</summary>
    public bool CanToggleKeyframes => CanToggleScenes;

    /// <summary>The Keyframes chip is lit.</summary>
    public bool KeyframesOn => ShowKeyframes && CanToggleKeyframes;

    /// <summary>The Waveform and Silence chips work without a file, not for a file without audio.</summary>
    public bool CanToggleAudio => !HasFile || Media is { AudioStreamCount: > 0 };

    public bool WaveformOn => ShowWaveform && CanToggleAudio;
    public bool SilencesOn => ShowSilences && CanToggleAudio;

    /// <summary>The Scenes chip works without a file (the choice is kept for the next one), not for a file without video.</summary>
    public bool CanToggleScenes => !HasFile || Media is { FrameRate: > 0 };

    /// <summary>The Scenes chip is lit.</summary>
    public bool ScenesOn => ShowScenes && CanToggleScenes;

    [RelayCommand]
    private void ToggleScenes() => ShowScenes = !ShowScenes;

    [RelayCommand]
    private void ToggleFrames() => ShowFrames = !ShowFrames;

    [RelayCommand]
    private void ToggleSnap() => SnapToKeyframes = !SnapToKeyframes;

    /// <summary>Fit: the whole file in view.</summary>
    [RelayCommand]
    private void ZoomFit() => ZoomLevel = 0;

    partial void OnSnapToKeyframesChanged(bool value) => ChipChanged();

    /// <summary>On: the open video's keyframes are scanned (and every later one's). Off: an unfinished scan stops.</summary>
    partial void OnShowKeyframesChanged(bool value)
    {
        if (!_showingChips)
        {
            if (value)
                Media?.ScanKeyframes();
            else
                Media?.StopKeyframes();
        }
        foreach (string name in (string[])[nameof(KeyframesOn), nameof(KeyframesTip)])
            OnPropertyChanged(name);
        ChipChanged();
    }

    partial void OnShowWaveformChanged(bool value)
    {
        OnPropertyChanged(nameof(WaveformOn));
        OnPropertyChanged(nameof(WaveformTip));
        AudioChipChanged();
    }

    partial void OnShowSilencesChanged(bool value)
    {
        OnPropertyChanged(nameof(SilencesOn));
        OnPropertyChanged(nameof(SilenceTip));
        AudioChipChanged();
    }

    /// <summary>
    /// The Waveform or Silence chip changed. Either on: the open video's audio is read (and every later one's), since
    /// silences are found in the waveform. Both off: an unfinished read stops.
    /// </summary>
    private void AudioChipChanged()
    {
        if (!_showingChips)
        {
            if (ShowWaveform || ShowSilences)
                Media?.ReadWaveform();
            else
                Media?.StopWaveform();
        }
        ChipChanged();
    }

    /// <summary>On: the open video's scene changes are found (and every later one's). Off: an unfinished search stops.</summary>
    partial void OnShowScenesChanged(bool value)
    {
        if (!_showingChips)
        {
            if (value)
                Media?.DetectScenes();
            else
                Media?.StopScenes();
        }
        foreach (string name in (string[])[nameof(ScenesOn), nameof(ScenesTip)])
            OnPropertyChanged(name);
        ChipChanged();
    }

    /// <summary>On: the open video's thumbnails are made (and every later one's). Off: an unfinished run stops.</summary>
    partial void OnShowFramesChanged(bool value)
    {
        if (!_showingChips)
        {
            if (value)
                Media?.ExtractThumbnails();
            else
                Media?.StopThumbnails();
        }
        ChipChanged();
    }

    private bool _showingChips;

    /// <summary>
    /// Shows the saved chips (the Transcript chip is "Transcribe when a video is opened"), saving and starting nothing:
    /// the next file opened is worked out as they say.
    /// </summary>
    internal void ShowTimelineChips(TimelineSettings chips, bool transcript)
    {
        _showingChips = true;
        ShowKeyframes = chips.Keyframes;
        ShowWaveform = chips.Waveform;
        ShowSilences = chips.Silences;
        ShowScenes = chips.Scenes;
        ShowFrames = chips.Frames;
        SnapToKeyframes = chips.Snap;
        ShowTranscriptLane = transcript;
        _showingChips = false;
    }

    /// <summary>Which chips are on, for Copy diagnostics: "keyframes off · waveform on · silence on · scenes off · frames off".</summary>
    internal string ChipSummary => string.Join(" · ",
        new[] { ("keyframes", ShowKeyframes), ("waveform", ShowWaveform), ("silence", ShowSilences), ("scenes", ShowScenes), ("frames", ShowFrames) }
            .Select(c => $"{c.Item1} {(c.Item2 ? "on" : "off")}"));

    /// <summary>A chip changed: redraw, and keep the choice for the next project (the design's screens keep nothing).</summary>
    private void ChipChanged()
    {
        RaiseTimelineChanged();
        if (!_showingChips && !IsDemo)
            Settings.SaveTimeline(new TimelineSettings(ShowKeyframes, ShowSilences, ShowScenes, SnapToKeyframes, ShowFrames, ShowWaveform));
    }

    // ---- Totals and status ---------------------------------------------------------------

    public double OutputDuration => Session.Project.OutputDuration;
    public string TotalText => TimeFormat.Duration(OutputDuration);
    public string ClipCountText => Clips.Count.ToString(CultureInfo.InvariantCulture);
    public bool HasNoClips => Clips.Count == 0;
    public string ClipSummary => $"{Clips.Count(c => c.IsIncluded)} of {Clips.Count} clips";

    /// <summary>Footer of the clip list: "4 of 5 clips", or "Nothing marked".</summary>
    public string OutputSummary => Clips.Count == 0 ? "Nothing marked" : ClipSummary;

    /// <summary>Output length as a full timecode (clip list footer).</summary>
    public string OutputTimecode => TimeFormat.Timecode(OutputDuration);
    public string KeptText => TimeFormat.WholeSeconds(OutputDuration);
    public string ExcludedText => HasFile ? TimeFormat.WholeSeconds(Session.Project.ExcludedDuration) : "0:00";

    public double ClaudeDelta => Claude.Log.Where(a => a.IsHighlighted).Sum(a => a.Delta);
    public bool HasClaudeDelta => ClaudeDelta != 0;

    public string ClaudeDeltaText =>
        (ClaudeDelta < 0 ? "−" : "+") + Math.Abs(ClaudeDelta).ToString("0.00", CultureInfo.InvariantCulture) + " s by Claude";

    public bool IsClaudeBusy => Claude.IsBusy;

    /// <summary>The MCP badge: "MCP · Off", "MCP · Waiting for Claude", "MCP · Claude connected", "MCP · Claude editing" or "MCP · In another window".</summary>
    public string McpText => Claude.McpText;

    /// <summary>Export needs an open file and at least one included clip.</summary>
    public bool CanExport => HasFile && Session.Project.IncludedClips.Any();

    /// <summary>The title bar's pill: "Exporting 45%" while an export runs, shown or not.</summary>
    public string ExportButtonText => Export.IsInProgress ? "Exporting " + Export.PercentText : "Export";

    /// <summary>ffmpeg availability shown in the status bar before a file is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusRight))]
    public partial string ToolStatus { get; set; } = "checking ffmpeg…";

    /// <summary>A short message (e.g. why an edit was refused) shown in the status bar for a few seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusRight))]
    public partial string? StatusMessage { get; set; }

    public string SaveStateText => IsDemo ? "autosaved"
        : ProjectPath is null ? "not saved"
        : IsDirty ? "unsaved changes"
        : LastSaveWasAuto ? "autosaved" : "saved";

    /// <summary>The file being opened (probed), shown in the status bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusRight), nameof(IsOpening))]
    public partial string? OpeningFile { get; set; }

    public bool IsOpening => OpeningFile is not null;

    public string StatusRight => StatusMessage
        ?? (OpeningFile is not null ? $"opening {OpeningFile}…"
            : HasFile
                ? (Media?.Activity is { } activity ? activity + " · " : "")
                  + $"{Export.ModeTitle.ToLowerInvariant()} · snap {(SnapToKeyframes ? "on" : "off")} · {SaveStateText}"
                : ToolStatus);

    /// <summary>
    /// Left side of the status bar: a message if there is one, otherwise the media details
    /// (with analysis progress and save state), or "No file open".
    /// </summary>
    public string StatusLeft
    {
        get
        {
            if (StatusMessage is { } message)
                return message;
            if (OpeningFile is not null)
                return $"Opening {OpeningFile}…";
            if (!HasFile)
                return ToolStatus.Contains("not found", StringComparison.Ordinal) ? "No file open  ·  " + ToolStatus : "No file open";
            string text = MediaInfoText;
            if (AnalysisActivity is { } activity)
                text += "  ·  " + activity;
            return IsDemo ? text : text + "  ·  " + SaveStateText;
        }
    }

    /// <summary>Source ranges not covered by any clip, in source order.</summary>
    public IReadOnlyList<TimeRange> ExcludedGaps() => HasFile ? Session.Project.UncoveredRanges() : [];

    // ---- Loading -------------------------------------------------------------------------

    /// <summary>Opens a project with its media preview. Clears the undo history.</summary>
    public void LoadProject(Project project, IMediaPreview media, string info, string? projectPath = null) =>
        LoadProject(project, new Dictionary<int, IMediaPreview> { [project.Source?.Id ?? SourceMedia.FirstId] = media }, info, projectPath);

    /// <summary>Opens a project with a preview for each of its videos (by video id). Clears the undo history.</summary>
    public void LoadProject(Project project, IReadOnlyDictionary<int, IMediaPreview> previews, string info, string? projectPath = null)
    {
        StopPlayback();
        SetPlayerLoaded(false);
        Export.Close();
        Select(null);
        MediaFileName = project.Source is { } s ? Path.GetFileName(s.Path) : "";
        MediaInfo = info;
        ProjectPath = projectPath;
        IsDirty = false;
        LastSaveWasAuto = false;
        ReplacePreviews(previews);
        var media = MediaFor(project);
        // Read once: the analysis may publish the keyframes between two reads, and the second would then
        // look already applied to OnPreviewChanged.
        var keyframes = media.Keyframes;
        Session.Keyframes = keyframes;
        _appliedKeyframes = keyframes;
        Session.Load(project);
        Media = media;
        Claude.HasMedia = true;
        CreateAudioLanes(project);
        Time = 0;
        RaiseProjectReplaced();
        if (_player is not null && media.IsPlayable && project.Source is not null)
            _ = LoadPlayerAsync(PlayerPath(project));
        else
            UnloadPlayer();
        if (Settings.TranscribeOnOpen)
            StartTranscription();
        else
            LoadCachedTranscript();
        // Only what the chips show is read; the processing screen waits for the keyframes and thumbnails, while the audio
        // (the Waveform and Silence chips) fills in in the background, so with the default chips the file plays at once.
        if (ShowKeyframes)
            media.ScanKeyframes();
        if (ShowWaveform || ShowSilences)
            media.ReadWaveform();
        if (ShowScenes)
            media.DetectScenes();
        if (ShowFrames)
            media.ExtractThumbnails();
        Processing.Track(IsDemo ? null : media, MediaFileName, info, project.TimelineDuration);
        // Saved before clips were kept apart: the export already plays each second once; say how to tidy the clips up.
        if (project.Overlaps() is [var (first, second), ..])
        {
            ShowMessage($"Clips {project.NumberOf(first.Id)} and {project.NumberOf(second.Id)} overlap. The export leaves out " +
                        "the repeated part; trim or join them (J) to tidy up.");
        }
    }

    /// <summary>Makes the recognizer (tests use a fake); sherpa-onnx if null.</summary>
    public Func<TranscriptionSetup, ISpeechRecognizer>? RecognizerFactory { get; set; }

    /// <summary>
    /// Transcribes the open file with the model chosen in Settings → Transcription (in the background, after the
    /// rest of the analysis), unless that transcript is done or under way. Returns why it cannot, or null.
    /// </summary>
    public string? StartTranscription()
    {
        if (IsDemo || !HasFile || Media is not { } media)
            return "No video is open.";
        if (TranscriptionSetup() is not { } setup)
            return "No transcription model is installed. The user can download one in Settings → Transcription (parakeet-tdt-0.6b-v3 is recommended).";
        media.StartTranscription(setup);
        return null;
    }

    /// <summary>Shows the open file's transcript from the cache, if the chosen model made one before.</summary>
    private void LoadCachedTranscript()
    {
        if (!IsDemo && HasFile && Media is { } media && TranscriptionSetup() is { } setup)
            media.LoadTranscript(setup);
    }

    private TranscriptionSetup? TranscriptionSetup()
    {
        if (Settings.ActiveModel is not { } model)
            return null;
        var setup = new TranscriptionSetup(model, Settings.DirectoryOf(model), Settings.LanguageCode ?? ModelCatalog.OnlyLanguage(model),
            Device: Settings.DeviceChoice);
        return RecognizerFactory is { } factory ? setup with { CreateRecognizer = () => factory(setup) } : setup;
    }

    /// <summary>Opens the file in the player; until it is ready (or if it fails) playback is simulated.</summary>
    private async Task LoadPlayerAsync(string path, bool play = false)
    {
        int generation = ++_playerGeneration;
        SetPlayerLoaded(false);
        _playerHasFile = true;
        try
        {
            await _player!.LoadAsync(path).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (MpvException e)
        {
            if (generation == _playerGeneration)
                ShowMessage("No playback for this file: " + e.Message);
            return;
        }
        if (generation != _playerGeneration || _player is null)
            return;
        SetPlayerLoaded(true);
        _player.SetVolume(Volume);
        _player.SetSpeed(Speed);
        ApplyAudioTracks();
        if (Time > 0)
            _player.Seek(Time);
        if (play)
        {
            _player.Play();
            IsPlaying = true;
        }
    }

    private void UnloadPlayer()
    {
        _playerGeneration++;
        if (_playerHasFile)
            _player?.Unload();
        _playerHasFile = false;
        SetPlayerLoaded(false);
    }

    private void SetPlayerLoaded(bool loaded)
    {
        if (_playerLoaded == loaded)
            return;
        _playerLoaded = loaded;
        OnPropertyChanged(nameof(HasPlayback));
    }

    /// <summary>The player moved (playing, a frame step or a seek that landed): follow it.</summary>
    private void OnPlayerStateChanged(object? sender, EventArgs e)
    {
        if (!HasPlayback || _player is not { } player)
            return;
        IsPlaying = player.IsPlaying;
        // Keep the playhead where the user put it until the seek lands.
        if (player.IsSeeking)
            return;
        _timeFromPlayer = true;
        try
        {
            Time = Math.Clamp(player.Position, 0, Duration);
        }
        finally
        {
            _timeFromPlayer = false;
        }
    }

    /// <summary>Everything derived from the project or its source changes when a project is (un)loaded.</summary>
    private void RaiseProjectReplaced()
    {
        foreach (string name in (string[])[nameof(ProjectName), nameof(WindowTitle), nameof(ProjectTitle), nameof(MediaInfoText),
                     nameof(HasSilenceData), nameof(HasSceneData), nameof(CanToggleScenes), nameof(ScenesOn), nameof(HasFile), nameof(IsEmpty),
                     nameof(TransportDurationText), nameof(HasSeveralVideos), nameof(PreviewParts),
                     nameof(Duration), nameof(DurationText), nameof(SourceLengthText), nameof(FrameRate), nameof(FrameText),
                     nameof(StatusRight)])
            OnPropertyChanged(name);
        RaiseTotals();
        RaiseTimelineChanged();
    }

    public void Unload()
    {
        StopPlayback();
        UnloadPlayer();
        Export.Close();
        Select(null);
        Media = null;
        ReplacePreviews(new Dictionary<int, IMediaPreview>());
        Processing.Track(null);
        MediaFileName = "";
        MediaInfo = "";
        ProjectPath = null;
        IsDirty = false;
        Session.Keyframes = [];
        Session.Load(Project.Empty);
        Claude.HasMedia = false;
        AudioLanes.Clear();
        Time = 0;
        RaiseProjectReplaced();
    }

    // ---- File commands -------------------------------------------------------------------

    [RelayCommand]
    private async Task OpenFile()
    {
        // As in the prototype, the empty demo screen "opens" the sample.
        if (IsDemo && IsEmpty)
        {
            DemoScenario.Apply(this, DesignScreen.Editing);
            return;
        }
        if (Dialogs is null)
            return;
        string? path = await Dialogs.PickMediaToOpenAsync().ConfigureAwait(true);
        if (path is not null)
            await OpenPath(path).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenProject()
    {
        if (Dialogs is null)
            return;
        string? path = await Dialogs.PickProjectToOpenAsync().ConfigureAwait(true);
        if (path is not null)
            await OpenProjectFileAsync(path).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenRecent(RecentFileViewModel? item)
    {
        if (item is null)
            return;
        // The design's recent entries have no file behind them; they open the sample project.
        if (item.Path.Length == 0)
            DemoScenario.OpenSample(this);
        else
            await OpenPath(item.Path).ConfigureAwait(true);
    }

    /// <summary>
    /// Opens a video, or a project when the path is a <c>.highlightcut.json</c> file (or an <c>.ourcut.json</c> one from
    /// before the rename): drag and drop, recent files, the command line.
    /// </summary>
    public Task OpenPath(string path) =>
        ProjectFile.IsProjectPath(path) ? OpenProjectFileAsync(path) : OpenMediaAsync(path);

    /// <summary>Opens a video as a new, empty project named after the file.</summary>
    public async Task OpenMediaAsync(string path)
    {
        var media = await OpenSourceAsync(path).ConfigureAwait(true);
        if (media is null)
            return;
        LeaveDemo();
        LoadProject(new Project(Path.GetFileNameWithoutExtension(path), media.Source, []), media.Preview, media.Summary);
        _openedPath = path;
        Remember(path, media.Source.Duration);
    }

    /// <summary>Opens a saved project and the video it edits.</summary>
    public async Task OpenProjectFileAsync(string path)
    {
        Project project;
        try
        {
            project = await ProjectFile.LoadAsync(path).ConfigureAwait(true);
        }
        catch (Exception e) when (e is ProjectFileException or IOException or UnauthorizedAccessException)
        {
            OpenFailed("Could not open the project: " + e.Message);
            return;
        }
        if (project.Source is null)
        {
            OpenFailed("The project has no source video.");
            return;
        }
        // Every video is probed; the probe is the truth about the file (it may have been re-encoded since), the clips are kept.
        var previews = new Dictionary<int, IMediaPreview>();
        string? summary = null;
        foreach (var source in project.Sources)
        {
            var media = await OpenSourceAsync(source.Path).ConfigureAwait(true);
            if (media is null)
            {
                foreach (var opened in previews.Values)
                    (opened as IDisposable)?.Dispose();
                return;
            }
            previews[source.Id] = media.Preview;
            summary ??= media.Summary;
            project = project.WithSource(media.Source with { Id = source.Id });
        }
        LeaveDemo();
        LoadProject(project, previews, summary!, path);
        _openedPath = path;
        Remember(path, project.TimelineDuration);
    }

    /// <summary>Opens a video or project for Claude (MCP). Returns why it failed, or null.</summary>
    public async Task<string?> OpenForClaudeAsync(string path)
    {
        _openError = null;
        _openedPath = null;
        await OpenPath(path).ConfigureAwait(true);
        return _openError ?? (_openedPath == path ? null : "Opening was cancelled: another file was opened meanwhile.");
    }

    private void OpenFailed(string message)
    {
        _openError = message;
        ShowMessage(message);
    }

    /// <summary>Probes a video. A newer open cancels an older one still probing.</summary>
    private async Task<OpenedMedia?> OpenSourceAsync(string path)
    {
        if (MediaOpener is null)
        {
            OpenFailed("Opening videos is not available.");
            return null;
        }
        if (_openCts is not null)
            await _openCts.CancelAsync().ConfigureAwait(true);
        var cts = new CancellationTokenSource();
        _openCts = cts;
        string name = Path.GetFileName(path);
        OpeningFile = name;
        try
        {
            var media = await MediaOpener.OpenAsync(path, cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested)
                return media;
            (media.Preview as IDisposable)?.Dispose();
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            OpenFailed($"{name} was not found. It may have been moved or deleted.");
            return null;
        }
        catch (Exception e) when (e is MediaToolException or IOException or UnauthorizedAccessException)
        {
            OpenFailed($"Could not open {name}: {e.Message}");
            return null;
        }
        finally
        {
            if (ReferenceEquals(_openCts, cts))
            {
                _openCts = null;
                OpeningFile = null;
            }
            cts.Dispose();
        }
    }

    private void Remember(string path, double duration)
    {
        if (RecentStore is null)
            return;
        RecentStore.Add(path, duration);
        LoadRecentFiles();
    }

    /// <summary>
    /// What the app opens at start: <paramref name="file"/> from the command line, or else the newest recent file when
    /// Settings → General → On startup says "Open the last project".
    /// </summary>
    public Task StartAsync(string? file)
    {
        if (file is not null)
            return OpenPath(file);
        if (!IsDemo && Settings.Startup == StartupAction.OpenLastProject && RecentFiles.FirstOrDefault() is { } last)
            return OpenPath(last.Path);
        return Task.CompletedTask;
    }

    /// <summary>Fills the empty screen's Recent list from <see cref="RecentStore"/>.</summary>
    public void LoadRecentFiles()
    {
        if (RecentStore is null)
            return;
        var now = DateTime.Now;
        RecentFiles.Clear();
        foreach (var file in RecentStore.Load())
            RecentFiles.Add(RecentFileViewModel.From(file, now));
    }

    [RelayCommand]
    public async Task SaveProject()
    {
        if (!HasFile)
            return;
        if (ProjectPath is null)
        {
            await SaveProjectAs().ConfigureAwait(true);
            return;
        }
        await SaveToAsync(ProjectPath, auto: false).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task SaveProjectAs()
    {
        if (!HasFile || Dialogs is null)
            return;
        string? path = await Dialogs.PickProjectSavePathAsync(ProjectFile.FileNameFor(Session.Project)).ConfigureAwait(true);
        if (path is not null)
            await SaveToAsync(path, auto: false).ConfigureAwait(true);
    }

    /// <summary>Saves the project to <paramref name="path"/>. Returns why it failed (also shown in the status bar), or null.</summary>
    public async Task<string?> SaveToAsync(string path, bool auto)
    {
        var project = Session.Project;
        try
        {
            await ProjectFile.SaveAsync(project, path).ConfigureAwait(true);
            ProjectPath = path;
            IsDirty = !ReferenceEquals(project, Session.Project);
            LastSaveWasAuto = auto;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            string message = "Could not save the project: " + e.Message;
            ShowMessage(message);
            return message;
        }
    }

    /// <summary>Saves for Claude (MCP): to <paramref name="path"/>, or where the project was saved before.</summary>
    public Task<string?> SaveForClaudeAsync(string? path) =>
        SavePathForClaude(path, out string? error) is { } full ? SaveToAsync(full, auto: false) : Task.FromResult(error);

    /// <summary>
    /// Where Claude's save goes: the full <paramref name="path"/>, or where the project was saved before. Null, with
    /// <paramref name="error"/> saying why, when it cannot save.
    /// </summary>
    public string? SavePathForClaude(string? path, out string? error)
    {
        error = null;
        if (!HasFile)
        {
            error = "No video is open.";
            return null;
        }
        if (path is null)
        {
            if (ProjectPath is null)
                error = "The project has not been saved yet; give a path ending in " + ProjectFile.Extension + ".";
            return ProjectPath;
        }
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"“{path}” is not a valid path.";
            return null;
        }
        if (!ProjectFile.IsProjectPath(full))
        {
            error = "Project files end in " + ProjectFile.Extension + ".";
            return null;
        }
        return full;
    }

    /// <summary>How long after the last edit a saved project is written again.</summary>
    public TimeSpan AutosaveDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    private void ScheduleAutosave()
    {
        if (!AutosaveEnabled || ProjectPath is null || IsDemo)
            return;
        _autosaveTimer?.Stop();
        _autosaveTimer = new DispatcherTimer(AutosaveDelay, DispatcherPriority.Background, async (_, _) =>
        {
            _autosaveTimer?.Stop();
            _autosaveTimer = null;
            if (IsDirty && ProjectPath is { } path)
                await SaveToAsync(path, auto: true).ConfigureAwait(true);
        });
        _autosaveTimer.Start();
    }

    // ---- Playback commands ---------------------------------------------------------------

    [RelayCommand]
    public void TogglePlay()
    {
        if (!HasFile)
            return;
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }
        if (HasPlayback)
        {
            // Play at the end starts over.
            if (Time >= Duration - 1 / FrameRate)
                SetTime(0);
            _player!.Play();
            IsPlaying = true;
            return;
        }
        IsPlaying = true;
        _lastTick = DateTime.UtcNow;
        _playTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) =>
        {
            var now = DateTime.UtcNow;
            double t = Time + (now - _lastTick).TotalSeconds * Speed;
            _lastTick = now;
            if (t >= Duration)
            {
                Time = Duration;
                StopPlayback();
                return;
            }
            _timeFromPlayer = true;
            try
            {
                SetTime(t);
            }
            finally
            {
                _timeFromPlayer = false;
            }
        });
        _playTimer.Start();
    }

    private void StopPlayback()
    {
        _playUntil = null;
        _playTimer?.Stop();
        _playTimer = null;
        if (HasPlayback && IsPlaying)
            _player!.Pause();
        IsPlaying = false;
    }

    [RelayCommand]
    private void StepBack() => StepFrame(forward: false);

    [RelayCommand]
    private void StepForward() => StepFrame(forward: true);

    /// <summary>One frame back or forward. The player decodes the exact neighbouring frame.</summary>
    private void StepFrame(bool forward)
    {
        if (HasPlayback)
        {
            // mpv pauses for a frame step.
            IsPlaying = false;
            _player!.StepFrame(forward);
            return;
        }
        SetTime(Time + (forward ? 1 : -1) / FrameRate);
    }

    public void StepSeconds(double seconds) => SetTime(Time + seconds);

    [RelayCommand]
    private void JumpIn()
    {
        if (SelectedClip is { } c)
            SetTime(c.Start);
    }

    [RelayCommand]
    private void JumpOut()
    {
        if (SelectedClip is { } c)
            SetTime(c.End);
    }

    [RelayCommand]
    private void CycleSpeed()
    {
        int i = Array.IndexOf(Speeds, Speed);
        Speed = Speeds[(i + 1) % Speeds.Length];
    }

    [RelayCommand]
    private void ZoomIn() => ZoomLevel = Math.Min(1, ZoomLevel + 0.1);

    [RelayCommand]
    private void ZoomOut() => ZoomLevel = Math.Max(0, ZoomLevel - 0.1);

    public void SetTime(double t) => Time = Math.Clamp(t, 0, Duration);

    // ---- Edit commands (all go through HighlightCut.Core) --------------------------------------

    [RelayCommand(CanExecute = nameof(CanUndo))]
    public void Undo() => Session.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    public void Redo() => Session.Redo();

    public bool CanUndo => Session.History.CanUndo;
    public bool CanRedo => Session.History.CanRedo;

    /// <summary>I: move the selected clip's in-point here, or start a new 10 s clip at the playhead.</summary>
    [RelayCommand]
    public void MarkIn()
    {
        if (!HasFile)
            return;
        if (SelectedClip is { } c && Time < c.End - EditRules.MinClipDuration)
        {
            // Stops at the end of the clip before, like dragging the handle.
            TryEdit(() => Session.Trim(c.Id, ClipEdge.In, Time));
            return;
        }
        TryEdit(() =>
        {
            // Up to 10 s, and not into the next clip or video: clips never share source time.
            var project = Session.Project;
            if (project.ToSource(Time) is not { } at)
                return;
            double videoEnd = project.GetSource(at.SourceId).Duration;
            if (project.FirstOverlapping(at.SourceId, at.Time, at.Time + EditRules.MinClipDuration) is { } under)
                throw new EditException($"The playhead is in clip {project.NumberOf(under.Id)}; select it to move its in-point.");
            var free = project.FreeRange(at.SourceId, at.Time, Math.Min(videoEnd, at.Time + 10))
                       ?? throw new EditException($"There is less than {EditRules.MinClipDuration} s before the next clip.");
            var clip = Session.AddClip(project.ToTimeline(at.SourceId, free.Start), project.ToTimeline(at.SourceId, free.End));
            Select(Find(clip.Id));
        });
    }

    /// <summary>O: move the selected clip's out-point here (up to the next clip).</summary>
    [RelayCommand]
    public void MarkOut()
    {
        if (SelectedClip is { } c && Time > c.Start + EditRules.MinClipDuration)
            TryEdit(() => Session.Trim(c.Id, ClipEdge.Out, Time));
    }

    /// <summary>S: split the selected clip (or the clip under the playhead) at the playhead.</summary>
    [RelayCommand]
    public void Split()
    {
        bool Inside(ClipViewModel x) => Time > x.Start + EditRules.MinClipDuration && Time < x.End - EditRules.MinClipDuration;
        var c = SelectedClip is { } s && Inside(s) ? s : Clips.FirstOrDefault(Inside);
        if (c is null)
            return;
        TryEdit(() => Select(Find(Session.Split(c.Id, Time).Id)));
    }

    /// <summary>
    /// J: join the selected clip with the clip right after it on the timeline, when the two touch or are less than
    /// <see cref="EditRules.JoinGap"/> apart. One undo step; the joined clip keeps the first clip's name.
    /// </summary>
    [RelayCommand]
    public void JoinWithNext()
    {
        if (SelectedClip is { } c)
            TryEdit(() => Select(Find(Session.JoinWithNext(c.Id).Id)));
    }

    /// <summary>The selected clip can be joined with the next one (for the menu).</summary>
    public bool CanJoinWithNext => SelectedClip is { } c && Session.CanJoinWithNext(c.Id);

    /// <summary>E: exclude the selected clip from the export, or keep it again.</summary>
    [RelayCommand]
    public void ToggleExclude()
    {
        if (SelectedClip is { } c)
            TryEdit(() => Session.SetIncluded(c.Id, !c.IsIncluded));
    }

    /// <summary>The × on a clip row: remove that clip from the project.</summary>
    [RelayCommand]
    public void RemoveClip(ClipViewModel? clip)
    {
        if (clip is null)
            return;
        if (ReferenceEquals(clip, SelectedClip))
            DeleteClip();
        else
            TryEdit(() => Session.Remove(clip.Id));
    }

    /// <summary>Split tool: cut the clip under <paramref name="t"/> there.</summary>
    public void SplitAt(ClipViewModel clip, double t)
    {
        SetTime(t);
        TryEdit(() => Select(Find(Session.Split(clip.Id, t).Id)));
    }

    /// <summary>Del / Shift+Del: remove the selected clip from the project.</summary>
    [RelayCommand]
    public void DeleteClip()
    {
        if (SelectedClip is not { } c)
            return;
        int index = Clips.IndexOf(c);
        if (TryEdit(() => Session.Remove(c.Id)))
            Select(Clips.Count == 0 ? null : Clips[Math.Min(index, Clips.Count - 1)]);
    }

    public void SetIncluded(ClipViewModel clip, bool included) => TryEdit(() => Session.SetIncluded(clip.Id, included));

    /// <summary>A colour picked from the clip's swatch or its context menu; undoable like any edit.</summary>
    public void SetColor(ClipViewModel clip, ClipColor color) => TryEdit(() => Session.SetColor(clip.Id, color));

    /// <summary>"+ Keep" on an excluded gap: add it back as a new clip.</summary>
    public void KeepRange(double from, double to) => TryEdit(() => Select(Find(Session.KeepRange(from, to).Id)));

    /// <summary>"+ Keep" on an excluded clip.</summary>
    public void Keep(ClipViewModel clip)
    {
        TryEdit(() => Session.SetIncluded(clip.Id, true));
        Select(clip);
    }

    public void MoveClip(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Clips.Count || to >= Clips.Count)
            return;
        TryEdit(() => Session.Move(Clips[from].Id, to));
    }

    /// <summary>
    /// Drags one end of a clip. It stops at the neighbouring clip and, within <paramref name="snapThreshold"/> seconds,
    /// snaps onto its edge (always) or onto a keyframe (with the Snap chip on). Calls with the same
    /// <paramref name="mergeKey"/> (one drag) undo as one step.
    /// </summary>
    public void Trim(ClipViewModel clip, bool inPoint, double t, double snapThreshold = 0, string? mergeKey = null) =>
        TryEdit(() => SetTime(Session.Trim(clip.Id, inPoint ? ClipEdge.In : ClipEdge.Out, t, snapThreshold, mergeKey,
            snapToKeyframes: SnapToKeyframes)));

    public void Select(ClipViewModel? clip)
    {
        SelectedClip = clip;
        foreach (var c in Clips)
            c.IsSelected = ReferenceEquals(c, clip);
        RaiseTimelineChanged();
    }

    /// <summary>Clicking a clip row selects it and moves the playhead into it.</summary>
    public void SelectFromList(ClipViewModel clip)
    {
        Select(clip);
        if (!clip.Contains(Time))
            SetTime(clip.Start);
    }

    /// <summary>Scrubbing the timeline moves the playhead and selects the clip under it.</summary>
    public void ScrubTo(double t, bool select)
    {
        SetTime(t);
        if (select && Clips.FirstOrDefault(c => c.Contains(Time)) is { } hit)
            Select(hit);
    }

    public ClipViewModel? Find(int id) => Clips.FirstOrDefault(c => c.Id == id);

    private bool TryEdit(Action edit)
    {
        try
        {
            edit();
            return true;
        }
        catch (EditException e)
        {
            ShowMessage(e.Message);
            return false;
        }
    }

    public void ShowMessage(string message)
    {
        StatusMessage = message;
        _messageTimer?.Stop();
        _messageTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) =>
        {
            StatusMessage = null;
            _messageTimer?.Stop();
        });
        _messageTimer.Start();
    }

    // ---- Syncing from Core ---------------------------------------------------------------

    private void OnSessionChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind != ProjectChangeKind.Loaded && !e.Previous.Sources.SequenceEqual(e.Current.Sources))
            OnSourcesChanged(e.Current);
        SyncClips(e.Current);
        SyncVideos(e.Current);
        SyncAudioLanes(e.Current);
        if (e.Kind != ProjectChangeKind.Loaded)
        {
            IsDirty = true;
            ScheduleAutosave();
        }
        if (!IsDemo)
            SyncHistoryLog();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        if (e.Previous.Name != e.Current.Name)
        {
            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(ProjectTitle));
        }
        if (Time > Duration)
            SetTime(Duration);
    }

    /// <summary>Updates the clip view models to match the project, keeping them (and selection) by id.</summary>
    private void SyncClips(Project project)
    {
        var ids = project.Clips.Select(c => c.Id).ToHashSet();
        for (int i = Clips.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Clips[i].Id))
                Clips.RemoveAt(i);
        }
        for (int i = 0; i < project.Clips.Count; i++)
        {
            var clip = project.Clips[i];
            int at = IndexOfClip(clip.Id);
            if (at < 0)
            {
                Clips.Insert(i, new ClipViewModel(clip, SetIncluded, SetColor, project));
                continue;
            }
            Clips[at].Update(clip, project);
            if (at != i)
                Clips.Move(at, i);
        }
        if (SelectedClip is not null && !Clips.Contains(SelectedClip))
            Select(null);
        RaiseTotals();
    }

    private int IndexOfClip(int id)
    {
        for (int i = 0; i < Clips.Count; i++)
        {
            if (Clips[i].Id == id)
                return i;
        }
        return -1;
    }

    /// <summary>Outside demo mode the Claude panel lists the edit history, with undo and redo per entry.</summary>
    private void SyncHistoryLog()
    {
        var entries = Session.History.Entries;
        var live = entries.ToHashSet();
        foreach (var stale in _historyItems.Keys.Where(k => !live.Contains(k)).ToList())
        {
            Claude.Log.Remove(_historyItems[stale]);
            _historyItems.Remove(stale);
        }
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!_historyItems.TryGetValue(entry, out var item))
            {
                item = new ClaudeLogItemViewModel(ClaudeLogKind.Action, entry.Description, HistoryMeta(entry), entry.ChangedClipIds,
                    entry.OutputDelta, changed: entry.Origin == EditOrigin.Assistant,
                    setUndone: undone => SetHistoryEntryUndone(entry, undone), at: entry.Time);
                _historyItems[entry] = item;
                Claude.Log.Insert(Math.Min(i, Claude.Log.Count), item);
            }
            item.IsUndone = i >= Session.History.Position || Session.IsReverted(entry);
        }
        Claude.Recount();
    }

    /// <summary>
    /// Undo on a card reverts just that edit (a new, undoable edit); Redo brings it back, either by
    /// reverting the revert or by redoing it if it was undone with Ctrl+Z.
    /// </summary>
    private void SetHistoryEntryUndone(HistoryEntry entry, bool undone)
    {
        bool ok = undone
            ? TryEdit(() => Session.Revert(entry))
            : Session.RevertOf(entry) is { } revert
                ? TryEdit(() => Session.Revert(revert))
                : TryEdit(() => Session.RedoThrough(entry));
        if (!ok)
            SyncHistoryLog();
    }

    private static string HistoryMeta(HistoryEntry entry)
    {
        double d = entry.OutputDelta;
        return Math.Abs(d) < 0.0005
            ? entry.Command.Name
            : $"{entry.Command.Name} · {(d < 0 ? "−" : "+")}{Math.Abs(d).ToString("0.00", CultureInfo.InvariantCulture)} s";
    }

    // ---- Change tracking -----------------------------------------------------------------

    partial void OnTimeChanged(double value)
    {
        FollowLaneVideo();
        OnPropertyChanged(nameof(CurrentClip));
        OnPropertyChanged(nameof(IsInClip));
        OnPropertyChanged(nameof(IsInExcluded));
        OnPropertyChanged(nameof(CurrentClipNumber));
        OnPropertyChanged(nameof(CurrentClipLabel));
        OnPropertyChanged(nameof(IsCurrentClipAi));
        RaiseTimelineChanged();
        CheckPlayUntil();
    }

    /// <summary>A new time set by the user (not by the player) moves the player there.</summary>
    partial void OnTimeChanged(double oldValue, double newValue)
    {
        if (!_timeFromPlayer && HasPlayback)
            _player!.Seek(newValue);
    }

    partial void OnVolumeChanged(double value)
    {
        if (HasPlayback)
            _player!.SetVolume(value);
    }

    partial void OnSpeedChanged(double value)
    {
        if (HasPlayback)
            _player!.SetSpeed(value);
    }

    partial void OnZoomLevelChanged(double value) => RaiseTimelineChanged();

    private void OnClipsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ClipViewModel c in e.OldItems)
                c.PropertyChanged -= OnClipPropertyChanged;
        }
        if (e.NewItems is not null)
        {
            foreach (ClipViewModel c in e.NewItems)
                c.PropertyChanged += OnClipPropertyChanged;
        }
        for (int i = 0; i < Clips.Count; i++)
            Clips[i].Number = i + 1;
        RaiseTotals();
    }

    private void OnClipPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClipViewModel.IsDragSource) or nameof(ClipViewModel.IsDropTarget)
            or nameof(ClipViewModel.IncludeToggle) or nameof(ClipViewModel.ColorBrush) or nameof(ClipViewModel.ColorName)
            or nameof(ClipViewModel.ColorChoices))
            return;
        RaiseTimelineChanged();
    }

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(OutputDuration));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(ClipCountText));
        OnPropertyChanged(nameof(HasNoClips));
        OnPropertyChanged(nameof(ClipSummary));
        OnPropertyChanged(nameof(OutputSummary));
        OnPropertyChanged(nameof(OutputTimecode));
        OnPropertyChanged(nameof(KeptText));
        OnPropertyChanged(nameof(ExcludedText));
        OnPropertyChanged(nameof(ExcludeLabel));
        OnPropertyChanged(nameof(SelectionInfo));
        OnPropertyChanged(nameof(CanJoinWithNext));
        OnPropertyChanged(nameof(CanExport));
        OnTimeChanged(Time);
    }

    /// <summary>Marks clips changed or being edited by Claude, from the activity log.</summary>
    public void ApplyClaudeHighlights()
    {
        var changed = Claude.Log.Where(a => a.IsHighlighted).SelectMany(a => a.ClipIds).ToHashSet();
        var working = Claude.Log.Where(a => a.IsLive).SelectMany(a => a.ClipIds).ToHashSet();
        var recent = Claude.Log.LastOrDefault(a => a.IsAction && a.IsChange) is { IsUndone: false } last
            ? last.ClipIds.ToHashSet()
            : [];
        foreach (var c in Clips)
        {
            c.IsAiChanged = changed.Contains(c.Id);
            c.IsAiWorking = working.Contains(c.Id);
            c.IsAiRecent = recent.Contains(c.Id);
        }
        OnPropertyChanged(nameof(ClaudeDelta));
        OnPropertyChanged(nameof(HasClaudeDelta));
        OnPropertyChanged(nameof(ClaudeDeltaText));
        OnPropertyChanged(nameof(IsCurrentClipAi));
        RaiseTimelineChanged();
    }

    public void RaiseTimelineChanged() => TimelineChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>What a click on a clip in the timeline does.</summary>
public enum TimelineTool
{
    Select,
    Split,
}
