namespace HighlightCut.App.Services;

/// <summary>Everything the settings dialog saves. A section missing from an older file reads as null: its defaults.</summary>
public sealed record AppSettings(
    TranscriptionSettings Transcription,
    GeneralSettings? General = null,
    PlaybackSettings? Playback = null,
    ExportDefaults? Export = null,
    KeyboardSettings? Keyboard = null,
    McpSettings? Mcp = null,
    TimelineSettings? Timeline = null)
{
    public static AppSettings Default { get; } = new(new TranscriptionSettings());
}
