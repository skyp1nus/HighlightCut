namespace HighlightCut.App.ViewModels;

/// <summary>The version whose What's new was seen (the app's own version is <see cref="AppVersion"/>).</summary>
public sealed partial class SettingsViewModel
{
    /// <summary>The version whose What's new was shown last, or that started first; null before there were versions.</summary>
    public string? LastSeenVersion => _settings.LastSeenVersion;

    internal void MarkVersionSeen(string version)
    {
        if (_settings.LastSeenVersion != version)
            UpdateSettings(s => s with { LastSeenVersion = version });
    }
}
