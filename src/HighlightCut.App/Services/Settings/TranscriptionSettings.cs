using System.Text.Json.Serialization;

namespace HighlightCut.App.Services;

/// <summary>Settings → Transcription.</summary>
/// <param name="TranscribeOnOpen">
/// Transcribe a video as soon as it is opened. Off by default: transcription goes through the whole video, so it waits
/// for the Transcript tab's Transcribe or for Claude. Saved under a new name, so files from when it was on by default
/// read as off.
/// </param>
/// <param name="FillerWords">Filler words by language code ("en", "uk"); null while they are the defaults.</param>
public sealed record TranscriptionSettings(
    string Engine = "Auto",
    string Model = "best",
    string Device = "Auto",
    string Language = "Auto-detect",
    string? ModelsFolder = null,
    [property: JsonPropertyName("transcribeWhenOpened")] bool TranscribeOnOpen = false,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? FillerWords = null);
