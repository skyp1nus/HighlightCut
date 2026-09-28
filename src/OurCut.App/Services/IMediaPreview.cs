using Avalonia;
using Avalonia.Media;
using OurCut.Core.Transcripts;
using OurCut.Media.Analysis;
using OurCut.Transcription;

namespace OurCut.App.Services;

/// <summary>How a frame is drawn: timeline thumbnail, greyed-out source strip, or the player.</summary>
public enum FrameLook
{
    Thumbnail,
    Excluded,
    Player,
}

/// <summary>
/// Everything the timeline and player need to draw a media file: frames, audio peaks and
/// keyframes. The demo implementation draws the design's placeholders; the real one draws
/// decoded thumbnails and waveforms.
/// </summary>
public interface IMediaPreview
{
    double Duration { get; }
    double FrameRate { get; }

    /// <summary>Keyframe times, sorted. Empty until the file has been scanned.</summary>
    IReadOnlyList<double> Keyframes { get; }

    /// <summary>Silent source ranges (every track quiet for a second or more), sorted. Grows while the audio is analysed.</summary>
    IReadOnlyList<OurCut.Core.Model.TimeRange> Silences => [];

    /// <summary>Scene change times, sorted. Grows while scene detection runs.</summary>
    IReadOnlyList<double> SceneChanges => [];

    /// <summary>What was said, as far as it has been transcribed; null before transcription starts.</summary>
    Transcript? Transcript => null;

    TranscriptState TranscriptState => TranscriptState.None;

    /// <summary>Part of the audio transcribed, 0..1.</summary>
    double TranscriptProgress => 0;

    /// <summary>Why transcription failed, if it did.</summary>
    string? TranscriptError => null;

    /// <summary>Transcribes the file (or reads the transcript from the cache) unless that is done or under way.</summary>
    void StartTranscription(TranscriptionSetup setup)
    {
    }

    /// <summary>Shows the transcript made earlier with this setup, if it was cached; transcribes nothing.</summary>
    void LoadTranscript(TranscriptionSetup setup)
    {
    }

    /// <summary>Stops a transcription under way (the Transcript chip turned off); a finished transcript stays.</summary>
    void StopTranscription()
    {
    }

    /// <summary>Silence detection has seen all of the audio (or given up).</summary>
    bool SilencesComplete => true;

    /// <summary>Scene detection has seen all of the video (or given up).</summary>
    bool ScenesComplete => true;

    /// <summary>Scene changes were asked for (or came from the cache); until then nothing is detected.</summary>
    bool ScenesRequested => true;

    /// <summary>Starts scene detection unless it was asked for already. It reads every frame, so it takes a while.</summary>
    void DetectScenes()
    {
    }

    /// <summary>Stops a scene detection under way (the Scenes chip turned off): what it found so far is dropped.</summary>
    void StopScenes()
    {
    }

    /// <summary>Thumbnails were asked for (the Frames chip); until then none are made.</summary>
    bool ThumbnailsRequested => true;

    /// <summary>Makes the timeline's thumbnails (or reads them from the cache) unless that is done or under way.</summary>
    void ExtractThumbnails()
    {
    }

    /// <summary>Stops making thumbnails (the Frames chip turned off): what was made so far is dropped.</summary>
    void StopThumbnails()
    {
    }

    /// <summary>Silences with other settings; null if the file has no audio or they cannot be computed.</summary>
    /// <param name="streams">Audio streams (0-based) that must all be quiet; all if null.</param>
    SilenceAnalysis? FindSilences(double minDuration, double? thresholdDb, IReadOnlyList<int>? streams) => null;

    /// <summary>Scene changes at another sensitivity; null if the file has no video or they cannot be computed.</summary>
    SceneAnalysis? FindSceneChanges(double threshold) => null;

    int AudioStreamCount { get; }

    /// <summary>Display width / height of the picture.</summary>
    double AspectRatio => 16.0 / 9.0;

    /// <summary>True for the design's shaded placeholder frames (demo mode).</summary>
    bool IsPlaceholder { get; }

    /// <summary>A real file the player can open (the design's sample has none behind it).</summary>
    bool IsPlayable => true;

    /// <summary>
    /// What is still being analysed, e.g. "analysing 45%"; null when everything is ready.
    /// </summary>
    string? Activity { get; }

    /// <summary>
    /// Keyframes, the waveform and thumbnails (if asked for) are still being read (the processing screen shows meanwhile).
    /// </summary>
    bool IsAnalysing => false;

    /// <summary>How far that reading is, 0..1.</summary>
    double AnalysisProgress => 1;

    /// <summary>The part the reading waits on most, e.g. "Reading the audio"; null when it is done.</summary>
    string? AnalysisStage => null;

    /// <summary>
    /// How long each part of the analysis took, for Copy diagnostics: "keyframes 0.3 s · thumbnails 0.4 s · waveform
    /// cached"; null before any part is done.
    /// </summary>
    string? AnalysisTimes => null;

    /// <summary>Why part of the analysis failed (e.g. a damaged audio stream); null if nothing did.</summary>
    string? AnalysisError { get; }

    /// <summary>Raised on the UI thread when more thumbnails, waveform or keyframes are available.</summary>
    event EventHandler? Changed;

    /// <summary>Peak level 0..1 of an audio stream between two source times.</summary>
    double AudioPeak(int stream, double startTime, double endTime);

    /// <summary>Draws the frame at <paramref name="time"/> into <paramref name="rect"/>.</summary>
    /// <param name="variant">Stable per-thumbnail number, used by the demo to vary placeholder shading.</param>
    void DrawFrame(DrawingContext context, Rect rect, double time, FrameLook look, int variant);
}
