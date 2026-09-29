using System.Text.Json.Serialization;

namespace HighlightCut.App.Services;

/// <summary>
/// The timeline toolbar's chips, kept for every project and every run. What is shown is what is worked out, so opening
/// a video reads nothing the chips do not show and the player starts at once: keyframes are scanned only while
/// <paramref name="Keyframes"/> is on, the audio is read only while <paramref name="Waveform"/> or
/// <paramref name="Silences"/> is, scene changes are found only while <paramref name="Scenes"/> is and thumbnails are
/// made only while <paramref name="Frames"/> is. A feature that needs keyframes or the audio (a lossless export, evening
/// out volumes, Claude's tools) reads them itself. (The Transcript chip is <see cref="TranscriptionSettings.TranscribeOnOpen"/>.)
/// </summary>
/// <param name="Keyframes">
/// Keyframe ticks on the video track (trims snap to them). Off by default; saved under a new name, so a file from when
/// it was on by default reads as off.
/// </param>
/// <param name="Silences">Silence bands on the audio track; they are found in the waveform. Off by default, like Keyframes.</param>
/// <param name="Scenes">Scene change markers; finding them reads every frame, so off by default.</param>
/// <param name="Snap">Trims snap to keyframes, once they are known.</param>
/// <param name="Frames">Thumbnails on the video track; the player shows the picture anyway, so off by default.</param>
/// <param name="Waveform">The audio waveform on the audio track; reading it goes through all the audio, so off by default.</param>
public sealed record TimelineSettings(
    [property: JsonPropertyName("keyframeTicks")] bool Keyframes = false,
    [property: JsonPropertyName("silenceBands")] bool Silences = false,
    bool Scenes = false,
    bool Snap = true,
    bool Frames = false,
    bool Waveform = false);
