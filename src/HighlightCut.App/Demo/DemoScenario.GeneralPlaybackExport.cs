using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Demo;

public static partial class DemoScenario
{
    // Settings → General, Playback and Export show the design's sample values on every screen.
    static partial void ApplyGeneralPlaybackExport(EditorViewModel editor, DesignScreen screen) =>
        editor.Settings.LoadGeneralPlaybackExportDemo();
}
