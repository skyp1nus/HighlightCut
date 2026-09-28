using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.App.Services;
using HighlightCut.Media.Export;

namespace HighlightCut.App.ViewModels;

// Settings → Export: the defaults the Export dialog starts with.
public sealed partial class SettingsViewModel
{
    // The design's sample, for the file name preview when no video is open.
    private const string SampleProject = "interview_final_v3";
    private static readonly string[] SampleLabels = ["Cold open", "Setup walkthrough", "Export demo", "Outro"];
    private static readonly DateOnly SampleDate = new(2026, 9, 25);

    private ChoiceSet<ExportDefaultMode>? _exportModeChoices;
    private ChoiceSet<ExportDefaultMode> ExportModeChoices => _exportModeChoices ??= new(
        [(ExportDefaultMode.Lossless, "Lossless"), (ExportDefaultMode.Reencode, "Re-encode")], v => DefaultExportMode = v, DefaultExportMode);
    public IReadOnlyList<ChoiceOption> ExportModeOptions => ExportModeChoices.Options;

    private ChoiceSet<ExportContainerDefault>? _containerChoices;
    private ChoiceSet<ExportContainerDefault> ContainerChoices => _containerChoices ??= new(
        [(ExportContainerDefault.SameAsSource, "Same as source"), (ExportContainerDefault.Mp4, "MP4"), (ExportContainerDefault.Mov, "MOV"),
            (ExportContainerDefault.Mkv, "MKV")], v => DefaultContainer = v, DefaultContainer);
    public IReadOnlyList<ChoiceOption> ContainerOptions => ContainerChoices.Options;

    private ChoiceSet<ExportFolderMode>? _exportFolderChoices;
    private ChoiceSet<ExportFolderMode> ExportFolderChoices => _exportFolderChoices ??= new(
        [(ExportFolderMode.NextToVideo, "Next to the video"), (ExportFolderMode.Fixed, "Fixed folder")], v => ExportFolder = v, ExportFolder);
    public IReadOnlyList<ChoiceOption> ExportFolderOptions => ExportFolderChoices.Options;

    private ChoiceSet<ExportAudioTracksMode>? _audioTrackChoices;
    private ChoiceSet<ExportAudioTracksMode> AudioTrackChoices => _audioTrackChoices ??= new(
        [(ExportAudioTracksMode.KeepAll, "Keep all"), (ExportAudioTracksMode.UnmutedOnly, "Only unmuted lanes")], v => ExportAudioTracks = v,
        ExportAudioTracks);
    public IReadOnlyList<ChoiceOption> AudioTrackOptions => AudioTrackChoices.Options;

    private ChoiceSet<ReencodeVideoPreset>? _reencodeVideoChoices;
    private ChoiceSet<ReencodeVideoPreset> ReencodeVideoChoices => _reencodeVideoChoices ??= new(
        [(ReencodeVideoPreset.H264Quality, "H.264 quality · CRF 18"), (ReencodeVideoPreset.H264Fast, "H.264 fast · CRF 23"),
            (ReencodeVideoPreset.H265, "H.265")], v => ReencodeVideo = v, ReencodeVideo);
    public IReadOnlyList<ChoiceOption> ReencodeVideoOptions => ReencodeVideoChoices.Options;

    private ChoiceSet<ReencodeAudioChoice>? _reencodeAudioChoices;
    private ChoiceSet<ReencodeAudioChoice> ReencodeAudioChoices => _reencodeAudioChoices ??= new(
        [(ReencodeAudioChoice.Copy, "Copy"), (ReencodeAudioChoice.Aac192, "AAC 192 kbps")], v => ReencodeAudio = v, ReencodeAudio);
    public IReadOnlyList<ChoiceOption> ReencodeAudioOptions => ReencodeAudioChoices.Options;

    private ChoiceSet<FileExistsAction>? _ifFileExistsChoices;
    private ChoiceSet<FileExistsAction> IfFileExistsChoices => _ifFileExistsChoices ??= new(
        [(FileExistsAction.AddNumber, "Add a number"), (FileExistsAction.Overwrite, "Overwrite"), (FileExistsAction.Ask, "Ask")],
        v => IfFileExists = v, IfFileExists);
    public IReadOnlyList<ChoiceOption> IfFileExistsOptions => IfFileExistsChoices.Options;

    private ChoiceSet<AfterExportAction>? _afterExportChoices;
    private ChoiceSet<AfterExportAction> AfterExportChoices => _afterExportChoices ??= new(
        [(AfterExportAction.ShowInFolder, "Show in folder"), (AfterExportAction.Nothing, "Nothing")], v => AfterExport = v, AfterExport);
    public IReadOnlyList<ChoiceOption> AfterExportOptions => AfterExportChoices.Options;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeNote))]
    public partial ExportDefaultMode DefaultExportMode { get; set; } = ExportDefaultMode.Lossless;

    public string ModeNote => DefaultExportMode == ExportDefaultMode.Lossless
        ? "Stream copy. In points snap to the keyframe before them."
        : "Frame-accurate cuts. Slower, and the video is compressed again.";

    [ObservableProperty]
    public partial ExportContainerDefault DefaultContainer { get; set; } = ExportContainerDefault.SameAsSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFixedFolder))]
    public partial ExportFolderMode ExportFolder { get; set; } = ExportFolderMode.NextToVideo;

    public bool IsFixedFolder => ExportFolder == ExportFolderMode.Fixed;

    [ObservableProperty]
    public partial string FixedExportFolder { get; set; } = ExportDefaults.DefaultFixedFolder;

    [ObservableProperty]
    public partial string FileNamePattern { get; set; } = ExportFileNames.DefaultPattern;

    public static IReadOnlyList<string> PatternTokens => ExportFileNames.Tokens;

    /// <summary>The merged file's name with the current pattern.</summary>
    [ObservableProperty]
    public partial string MergedNamePreview { get; private set; } = "";

    /// <summary>The first separate files' names with the current pattern.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> SeparateNamePreview { get; private set; } = [];

    [ObservableProperty]
    public partial bool MergeFiles { get; set; } = true;

    [ObservableProperty]
    public partial bool ChaptersFromLabels { get; set; } = true;

    [ObservableProperty]
    public partial ExportAudioTracksMode ExportAudioTracks { get; set; } = ExportAudioTracksMode.KeepAll;

    [ObservableProperty]
    public partial ReencodeVideoPreset ReencodeVideo { get; set; } = ReencodeVideoPreset.H264Quality;

    [ObservableProperty]
    public partial ReencodeAudioChoice ReencodeAudio { get; set; } = ReencodeAudioChoice.Copy;

    [ObservableProperty]
    public partial bool UseGpuEncoder { get; set; } = true;

    [ObservableProperty]
    public partial string GpuEncoderNote { get; set; } = GpuNote(null);

    /// <summary>The GPU encoder found on this computer (<see cref="DetectGpuEncoderAsync"/>); null for none.</summary>
    [ObservableProperty]
    public partial GpuEncoderSupport? GpuEncoder { get; private set; }

    [ObservableProperty]
    public partial FileExistsAction IfFileExists { get; set; } = FileExistsAction.AddNumber;

    [ObservableProperty]
    public partial AfterExportAction AfterExport { get; set; } = AfterExportAction.ShowInFolder;

    partial void OnDefaultExportModeChanged(ExportDefaultMode value)
    {
        _exportModeChoices?.Select(value);
        SaveExportDefaults();
    }

    partial void OnDefaultContainerChanged(ExportContainerDefault value)
    {
        _containerChoices?.Select(value);
        SaveExportDefaults();
        RefreshNamePreview();
    }

    partial void OnExportFolderChanged(ExportFolderMode value)
    {
        _exportFolderChoices?.Select(value);
        SaveExportDefaults();
    }

    partial void OnFixedExportFolderChanged(string value) => SaveExportDefaults();

    partial void OnFileNamePatternChanged(string value)
    {
        SaveExportDefaults();
        RefreshNamePreview();
    }

    partial void OnMergeFilesChanged(bool value) => SaveExportDefaults();

    partial void OnChaptersFromLabelsChanged(bool value) => SaveExportDefaults();

    partial void OnExportAudioTracksChanged(ExportAudioTracksMode value)
    {
        _audioTrackChoices?.Select(value);
        SaveExportDefaults();
    }

    partial void OnReencodeVideoChanged(ReencodeVideoPreset value)
    {
        _reencodeVideoChoices?.Select(value);
        SaveExportDefaults();
    }

    partial void OnReencodeAudioChanged(ReencodeAudioChoice value)
    {
        _reencodeAudioChoices?.Select(value);
        SaveExportDefaults();
    }

    partial void OnUseGpuEncoderChanged(bool value) => SaveExportDefaults();

    partial void OnIfFileExistsChanged(FileExistsAction value)
    {
        _ifFileExistsChoices?.Select(value);
        SaveExportDefaults();
    }

    partial void OnAfterExportChanged(AfterExportAction value)
    {
        _afterExportChoices?.Select(value);
        SaveExportDefaults();
    }

    /// <summary>Saves the defaults and hands them to the Export dialog (loading hands them over once, at its end).</summary>
    private void SaveExportDefaults()
    {
        var d = ToExportDefaults();
        UpdateSettings(s => s with { Export = d });
        if (!_loading)
            _editor.Export.Defaults = d;
    }

    internal ExportDefaults ToExportDefaults() => new(DefaultExportMode, DefaultContainer, ExportFolder,
        FixedExportFolder == ExportDefaults.DefaultFixedFolder ? null : FixedExportFolder, FileNamePattern, MergeFiles, ChaptersFromLabels,
        ExportAudioTracks, ReencodeVideo, ReencodeAudio, UseGpuEncoder, IfFileExists, AfterExport);

    /// <summary>Shows <paramref name="d"/>; a value the dialog does not offer reads as its default.</summary>
    internal void LoadExportDefaults(ExportDefaults? d)
    {
        d ??= new();
        DefaultExportMode = Enum.IsDefined(d.Mode) ? d.Mode : ExportDefaultMode.Lossless;
        DefaultContainer = Enum.IsDefined(d.Container) ? d.Container : ExportContainerDefault.SameAsSource;
        ExportFolder = Enum.IsDefined(d.Folder) ? d.Folder : ExportFolderMode.NextToVideo;
        FixedExportFolder = string.IsNullOrWhiteSpace(d.FixedFolder) ? ExportDefaults.DefaultFixedFolder : d.FixedFolder;
        FileNamePattern = string.IsNullOrWhiteSpace(d.FileNamePattern) ? ExportFileNames.DefaultPattern : d.FileNamePattern;
        MergeFiles = d.Merge;
        ChaptersFromLabels = d.Chapters;
        ExportAudioTracks = Enum.IsDefined(d.AudioTracks) ? d.AudioTracks : ExportAudioTracksMode.KeepAll;
        ReencodeVideo = Enum.IsDefined(d.Video) ? d.Video : ReencodeVideoPreset.H264Quality;
        ReencodeAudio = Enum.IsDefined(d.Audio) ? d.Audio : ReencodeAudioChoice.Copy;
        UseGpuEncoder = d.UseGpuEncoder;
        IfFileExists = Enum.IsDefined(d.IfExists) ? d.IfExists : FileExistsAction.AddNumber;
        AfterExport = Enum.IsDefined(d.AfterExport) ? d.AfterExport : AfterExportAction.ShowInFolder;
        _editor.Export.Defaults = ToExportDefaults();
    }

    [RelayCommand]
    private async Task ChangeExportFolder()
    {
        if (_editor.Dialogs is null)
            return;
        string? folder = await _editor.Dialogs.PickFolderAsync("Export folder", FixedExportFolder).ConfigureAwait(true);
        if (folder is not null)
            FixedExportFolder = folder;
    }

    /// <summary>Puts <paramref name="token"/> in place of the text from <paramref name="start"/> to <paramref name="end"/>; returns the caret after it.</summary>
    public int InsertPatternToken(string token, int start, int end)
    {
        string pattern = FileNamePattern;
        (start, end) = (Math.Clamp(start, 0, pattern.Length), Math.Clamp(end, 0, pattern.Length));
        if (start > end)
            (start, end) = (end, start);
        FileNamePattern = pattern[..start] + token + pattern[end..];
        return start + token.Length;
    }

    /// <summary>Names from the pattern for the open video, or for the design's sample when none is open.</summary>
    internal void RefreshNamePreview()
    {
        bool sample = _editor.IsDemo || !_editor.HasFile;
        string project = sample ? SampleProject : _editor.ProjectName;
        IReadOnlyList<string> labels = sample ? SampleLabels : [.. _editor.Clips.Where(c => c.IsIncluded).Select(c => c.Label)];
        var date = _editor.IsDemo ? SampleDate : DateOnly.FromDateTime(DateTime.Today);
        string natural = (_editor.Media as MediaPreview)?.Info.NaturalExtension ?? "mp4";
        string ext = "." + ToExportDefaults().ContainerFor(natural).ToLowerInvariant();
        MergedNamePreview = ExportFileNames.Fill(FileNamePattern, project, 1, "", date, merged: true) + ext;
        SeparateNamePreview = [.. labels.Take(4).Select((label, i) => ExportFileNames.Fill(FileNamePattern, project, i + 1, label, date, merged: false) + ext)];
    }

    /// <summary>
    /// Looks for a GPU encoder (a few seconds of test encodes at most) and hands it to the Export dialog. The app runs it
    /// once at start.
    /// </summary>
    /// <param name="detect">Finds the encoder; <see cref="GpuEncoderProbe.DetectAsync"/> by default.</param>
    public async Task DetectGpuEncoderAsync(Func<CancellationToken, Task<GpuEncoderSupport?>>? detect = null,
        CancellationToken cancellationToken = default)
    {
        GpuEncoderNote = "Looking for a GPU encoder…";
        var found = await (detect ?? (ct => GpuEncoderProbe.DetectAsync(cancellationToken: ct)))(cancellationToken).ConfigureAwait(true);
        GpuEncoder = found;
        GpuEncoderNote = GpuNote(found);
        _editor.Export.Gpu = found;
    }

    private static string GpuNote(GpuEncoderSupport? found) => found switch
    {
        null => "No GPU encoder found. Re-encoding uses the CPU.",
        { Hevc: false } => $"Detected: {found.Encoder.Name}. H.265 still uses the CPU.",
        _ => "Detected: " + found.Encoder.Name,
    };
}
