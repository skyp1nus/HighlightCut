namespace HighlightCut.App.Demo;

/// <summary>The screens of the design (its view switcher), used for demo mode and UI tests.</summary>
public enum DesignScreen
{
    Empty,
    Editing,
    Ai,
    Export,
    Exporting,

    /// <summary>Settings → Transcription.</summary>
    Settings,
    Transcript,
    Transcribing,
    NoModel,
    ClaudeRequest,
    ClaudeExporting,
    ClaudeExportFailed,
    SettingsGeneral,
    SettingsPlayback,
    SettingsExport,
    SettingsKeyboard,
    SettingsKeyboardRecording,
    SettingsKeyboardConflict,
    SettingsMcp,
}
