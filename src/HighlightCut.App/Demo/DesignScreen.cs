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

    /// <summary>The welcome tour over the empty editor (HighlightCut Onboarding.dc.html, 1b), first step.</summary>
    Welcome,

    /// <summary>The welcome tour's last step, Connect Claude, waiting for Claude.</summary>
    WelcomeClaude,

    /// <summary>The welcome tour's Transcript step with no model installed: it offers Parakeet's download.</summary>
    WelcomeTranscript,

    /// <summary>The welcome tour's Transcript step while Parakeet downloads.</summary>
    WelcomeDownloading,
}
