namespace HighlightCut.App.ViewModels;

/// <summary>The welcome tour's one saved setting.</summary>
public sealed partial class SettingsViewModel
{
    /// <summary>The welcome tour was finished or skipped once.</summary>
    public bool WelcomeTourSeen => _settings.WelcomeTourSeen;

    internal void MarkWelcomeTourSeen()
    {
        if (!_settings.WelcomeTourSeen)
            UpdateSettings(s => s with { WelcomeTourSeen = true });
    }
}
