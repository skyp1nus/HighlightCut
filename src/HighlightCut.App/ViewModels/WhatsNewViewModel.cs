using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.App.Services;

namespace HighlightCut.App.ViewModels;

/// <summary>What opens over the editor at start.</summary>
public enum StartDialog
{
    None,
    Tour,
    WhatsNew,
}

/// <summary>
/// "What's new in HighlightCut X": the notes of CHANGELOG.md since the version the user saw last. It opens once after
/// an update (the welcome tour is for new users), and again from the project menu → What's new.
/// </summary>
public sealed partial class WhatsNewViewModel(EditorViewModel editor) : ViewModelBase
{
    /// <summary>The notes; the app's own CHANGELOG.md unless a test gives others.</summary>
    internal Changelog Changelog { get; set; } = Changelog.Embedded;

    /// <summary>This build's version ("0.1.0", or "0.1.0-dev.42" from CI); a test can pretend to be another.</summary>
    internal string CurrentVersion { get; set; } = AppVersion.Text;

    private Version CurrentRelease => AppVersion.ReleaseOf(CurrentVersion) ?? AppVersion.Release;

    public SettingsViewModel Settings => editor.Settings;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    public string Title => $"What’s new in HighlightCut {CurrentRelease.ToString(3)}";

    /// <summary>The sections shown, newest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChangelogRelease> Releases { get; private set; } = [];

    /// <summary>
    /// Decides what opens at start and opens it. A new user (no settings file, no recent files) gets the welcome tour,
    /// as before, and this version counts as seen. Someone who used an older version (or one from before versions)
    /// gets What's new, also with a file to open; it counts as the tour too. The same version again opens nothing,
    /// except the tour for someone who has not had it yet. Never in demo mode.
    /// </summary>
    /// <param name="file">A video or project from the command line.</param>
    /// <param name="settingsExisted">There was a settings file before this start (<see cref="AppSettingsStore.Existed"/>).</param>
    public StartDialog OpenAtStart(string? file, bool settingsExisted)
    {
        if (editor.IsDemo)
            return StartDialog.None;
        bool existing = settingsExisted || editor.RecentFiles.Count > 0;
        if (existing && IsNewerThanSeen())
        {
            Settings.MarkWelcomeTourSeen();
            if (Open(Changelog.Since(AppVersion.ReleaseOf(Settings.LastSeenVersion), CurrentRelease)))
                return StartDialog.WhatsNew;
            Settings.MarkVersionSeen(CurrentVersion);
            return StartDialog.None;
        }
        if (!existing)
            Settings.MarkVersionSeen(CurrentVersion);
        if (!editor.Tour.ShouldOpenAtStart(file))
            return StartDialog.None;
        editor.Tour.Open();
        return StartDialog.Tour;
    }

    // Older than this version, or saved before there were versions. A dev build of the same version is not newer.
    private bool IsNewerThanSeen() => AppVersion.ReleaseOf(Settings.LastSeenVersion) is not { } seen || seen < CurrentRelease;

    /// <summary>The project menu → What's new: this version's notes.</summary>
    [RelayCommand]
    public void Show()
    {
        if (!Open([.. Changelog.Releases.Where(r => r.Version == CurrentRelease && !r.IsEmpty)]))
            Open([.. Changelog.Releases.Where(r => r.Version is not null && !r.IsEmpty).Take(1)]);
    }

    private bool Open(IReadOnlyList<ChangelogRelease> releases)
    {
        if (releases.Count == 0)
            return false;
        Releases = releases;
        IsOpen = true;
        return true;
    }

    /// <summary>Got it, Enter, Esc: closes it and saves that this version's notes were seen.</summary>
    [RelayCommand]
    public void Close()
    {
        if (!IsOpen)
            return;
        IsOpen = false;
        if (!editor.IsDemo)
            Settings.MarkVersionSeen(CurrentVersion);
    }

    /// <summary>Take the tour: closes this and opens the welcome tour.</summary>
    [RelayCommand]
    private void TakeTour()
    {
        Close();
        editor.Tour.Open();
    }

    /// <summary>Hides it without saving anything (demo screens start from here).</summary>
    public void Reset() => IsOpen = false;
}
