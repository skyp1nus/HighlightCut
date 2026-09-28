using HighlightCut.App.Services;

namespace HighlightCut.App.ViewModels;

// The timeline toolbar's chips: not in the dialog, but saved with the rest.
public sealed partial class SettingsViewModel
{
    /// <summary>Saves the chips as the editor shows them.</summary>
    internal void SaveTimeline(TimelineSettings chips) => UpdateSettings(s => s with { Timeline = chips });

    /// <summary>Puts the saved chips back on the editor (at start, and when the demo is left).</summary>
    internal void ApplyTimeline() => _editor.ShowTimelineChips(_settings.Timeline ?? new TimelineSettings(), TranscribeOnOpen);
}
