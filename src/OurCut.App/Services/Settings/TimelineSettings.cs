namespace OurCut.App.Services;

/// <summary>
/// The timeline toolbar's chips, kept for every project and every run. What is shown is what is worked out: scene
/// changes are found in every video opened only while <paramref name="Scenes"/> is on, and thumbnails are made only
/// while <paramref name="Frames"/> is. (The Transcript chip is <see cref="TranscriptionSettings.TranscribeOnOpen"/>.)
/// </summary>
/// <param name="Keyframes">Keyframe ticks on the video track.</param>
/// <param name="Silences">Silence bands on the audio track.</param>
/// <param name="Scenes">Scene change markers; finding them reads every frame, so off by default.</param>
/// <param name="Snap">Trims snap to keyframes.</param>
/// <param name="Frames">Thumbnails on the video track; the player shows the picture anyway, so off by default.</param>
public sealed record TimelineSettings(bool Keyframes = true, bool Silences = true, bool Scenes = false, bool Snap = true,
    bool Frames = false);
