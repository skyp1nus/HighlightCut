using HighlightCut.Media.Export;

namespace HighlightCut.App.Services;

public enum ExportDefaultMode
{
    Lossless,
    Reencode,
}

public enum ExportContainerDefault
{
    SameAsSource,
    Mp4,
    Mov,
    Mkv,
}

public enum ExportFolderMode
{
    NextToVideo,
    Fixed,
}

public enum ExportAudioTracksMode
{
    KeepAll,
    UnmutedOnly,
}

public enum ReencodeVideoPreset
{
    H264Quality,
    H264Fast,
    H265,
}

public enum ReencodeAudioChoice
{
    Copy,
    Aac192,
}

public enum FileExistsAction
{
    AddNumber,
    Overwrite,
    Ask,
}

public enum AfterExportAction
{
    ShowInFolder,
    Nothing,
}

/// <summary>Settings → Export: what the Export dialog starts with.</summary>
/// <param name="FixedFolder">Used when <paramref name="Folder"/> is Fixed; null is <see cref="DefaultFixedFolder"/>.</param>
public sealed record ExportDefaults(
    ExportDefaultMode Mode = ExportDefaultMode.Lossless,
    ExportContainerDefault Container = ExportContainerDefault.SameAsSource,
    ExportFolderMode Folder = ExportFolderMode.NextToVideo,
    string? FixedFolder = null,
    string FileNamePattern = ExportFileNames.DefaultPattern,
    bool Merge = true,
    bool Chapters = true,
    ExportAudioTracksMode AudioTracks = ExportAudioTracksMode.KeepAll,
    ReencodeVideoPreset Video = ReencodeVideoPreset.H264Quality,
    ReencodeAudioChoice Audio = ReencodeAudioChoice.Copy,
    bool UseGpuEncoder = true,
    FileExistsAction IfExists = FileExistsAction.AddNumber,
    AfterExportAction AfterExport = AfterExportAction.ShowInFolder)
{
    /// <summary>The fixed folder until the user picks one: Videos, or the home folder where there is none.</summary>
    public static string DefaultFixedFolder { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) is { Length: > 0 } videos
            ? videos
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>"MP4", "MOV" or "MKV": the chosen container, or one that can hold the source's streams.</summary>
    public string ContainerFor(string? naturalExtension) => Container switch
    {
        ExportContainerDefault.Mp4 => "MP4",
        ExportContainerDefault.Mov => "MOV",
        ExportContainerDefault.Mkv => "MKV",
        _ => naturalExtension switch
        {
            null or "mp4" or "m4v" => "MP4",
            "mov" => "MOV",
            _ => "MKV",
        },
    };

    /// <summary>Where exports of <paramref name="sourcePath"/> go.</summary>
    public string? FolderFor(string sourcePath) =>
        Folder == ExportFolderMode.Fixed ? FixedFolder ?? DefaultFixedFolder : Path.GetDirectoryName(sourcePath);
}
