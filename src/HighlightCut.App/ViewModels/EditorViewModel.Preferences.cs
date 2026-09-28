using CommunityToolkit.Mvvm.ComponentModel;

namespace HighlightCut.App.ViewModels;

// What Settings → General and Playback change in the editor.
public sealed partial class EditorViewModel
{
    /// <summary>Settings → General → Autosave: a saved project is written again after each change.</summary>
    [ObservableProperty]
    public partial bool AutosaveEnabled { get; set; } = true;

    partial void OnAutosaveEnabledChanged(bool value)
    {
        if (!value)
        {
            _autosaveTimer?.Stop();
            _autosaveTimer = null;
        }
        else if (IsDirty)
        {
            ScheduleAutosave();
        }
    }

    /// <summary>Shift+←/→ step in seconds (Settings → Playback → Jump length).</summary>
    public double JumpSeconds { get; set; } = 1;

    /// <summary>Jumps <see cref="JumpSeconds"/> back (-1) or forward (1).</summary>
    public void Jump(int direction) => StepSeconds(direction * JumpSeconds);
}
