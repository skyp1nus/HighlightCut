namespace HighlightCut.App.Services;

/// <summary>File pickers used by the editor. Implemented by the main window; faked in tests.</summary>
public interface IFileDialogs
{
    /// <summary>Returns the chosen video file, or null if cancelled.</summary>
    Task<string?> PickMediaToOpenAsync();

    /// <summary>Returns the video files chosen to add to the project (several allowed), or none if cancelled.</summary>
    Task<IReadOnlyList<string>> PickMediaToAddAsync() => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Returns the chosen <c>.highlightcut.json</c> file, or null if cancelled.</summary>
    Task<string?> PickProjectToOpenAsync();

    /// <summary>Returns where to save the project, or null if cancelled.</summary>
    Task<string?> PickProjectSavePathAsync(string suggestedFileName);

    /// <summary>Returns the chosen folder, or null if cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? startFolder);
}
