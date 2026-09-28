namespace HighlightCut.App.Services;

public enum StartupAction
{
    OpenLastProject,
    StartEmpty,
}

/// <summary>Settings → General.</summary>
public sealed record GeneralSettings(
    StartupAction Startup = StartupAction.OpenLastProject,
    bool Autosave = true,
    int RecentFilesLimit = 10);
