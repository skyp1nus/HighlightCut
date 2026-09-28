using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HighlightCut.Core.Serialization;

namespace HighlightCut.App.Services;

/// <summary><see cref="IFileDialogs"/> on top of Avalonia's storage provider.</summary>
public sealed class StorageFileDialogs(TopLevel topLevel) : IFileDialogs
{
    private static readonly FilePickerFileType Videos = new("Video files")
    {
        Patterns = ["*.mp4", "*.mov", "*.mkv", "*.webm", "*.m4v", "*.avi", "*.ts", "*.mts", "*.m2ts", "*.mpg", "*.mpeg", "*.flv", "*.wmv"],
        MimeTypes = ["video/*"],
    };

    private static readonly FilePickerFileType Projects = new("HighlightCut project")
    {
        Patterns = ["*" + ProjectFile.Extension, "*" + ProjectFile.LegacyExtension],
        MimeTypes = ["application/json"],
    };

    public async Task<string?> PickMediaToOpenAsync()
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open video",
            AllowMultiple = false,
            FileTypeFilter = [Videos, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickProjectToOpenAsync()
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open project",
            AllowMultiple = false,
            FileTypeFilter = [Projects],
        }).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickProjectSavePathAsync(string suggestedFileName)
    {
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save project",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = ProjectFile.Extension[1..],
            FileTypeChoices = [Projects],
            ShowOverwritePrompt = true,
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        var start = startFolder is not null && Directory.Exists(startFolder)
            ? await topLevel.StorageProvider.TryGetFolderFromPathAsync(startFolder).ConfigureAwait(true)
            : null;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        }).ConfigureAwait(true);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
