using HighlightCut.Core.Editing;
using HighlightCut.Core.Model;
using HighlightCut.Core.Transcripts;

namespace HighlightCut.Mcp;

/// <summary>
/// The running editor as the MCP tools see it. <see cref="EditorSession"/> is not thread-safe, so
/// every tool runs its work through <see cref="RunAsync{T}"/>, which the app executes on its UI thread.
/// </summary>
public interface IEditorHost
{
    Task<T> RunAsync<T>(Func<IEditorContext, Task<T>> action);
}

/// <summary>What a tool can read and do inside <see cref="IEditorHost.RunAsync{T}"/>.</summary>
public interface IEditorContext
{
    /// <summary>The project and its undo history. Tools edit through it with <see cref="EditOrigin.Assistant"/>.</summary>
    EditorSession Session { get; }

    /// <summary>A video is open.</summary>
    bool HasFile { get; }

    /// <summary>e.g. "keynote.mp4 · 4K · 29.97 fps"; null without a file.</summary>
    string? SourceSummary { get; }

    /// <summary>Where the project is saved, if it is.</summary>
    string? ProjectPath { get; }

    /// <summary>Playhead position in seconds.</summary>
    double Playhead { get; }

    int? SelectedClipId { get; }
    bool IsPlaying { get; }

    /// <summary>Keyframe times of the source, sorted (empty until scanned).</summary>
    IReadOnlyList<double> Keyframes { get; }

    /// <summary>Stretches where every chosen audio track stays quiet; null when the video has no audio.</summary>
    /// <param name="minDuration">Shortest silence, in seconds.</param>
    /// <param name="thresholdDb">Peak level (dBFS) that counts as silent; automatic (from the noise floor) if null.</param>
    /// <param name="streams">Audio streams (0-based) that must all be quiet; all if null.</param>
    SilenceReport? FindSilences(double minDuration, double? thresholdDb, IReadOnlyList<int>? streams);

    /// <summary>Scene changes at a sensitivity (ffmpeg <c>scdet</c> scale); null when there is no video.</summary>
    SceneReport? FindSceneChanges(double threshold);

    /// <summary>What is still being analysed, e.g. "analysing 45%"; null when done.</summary>
    string? AnalysisStatus { get; }

    /// <summary>What is said in the video, as far as it has been transcribed, and how far that is.</summary>
    TranscriptStatus TranscriptStatus { get; }

    /// <summary>The user's filler words (Settings → Transcription), every language: what find/cut_filler_words look for.</summary>
    IReadOnlyList<string> FillerWords => Core.Transcripts.FillerWords.All(Core.Transcripts.FillerWords.Defaults);

    /// <summary>Starts transcribing if it has not started. Returns why it cannot (e.g. no model installed), or null.</summary>
    string? StartTranscription();

    /// <summary>
    /// Starts exporting the included clips with the Export dialog's settings, changed where <paramref name="request"/> says;
    /// the editor shows the progress in its Claude panel. May first wait for the user to allow it.
    /// Returns why it cannot start or why the user declined, or null.
    /// </summary>
    Task<string?> StartExportAsync(ExportRequest request, CancellationToken cancellationToken);

    /// <summary>The running export, or how the latest one ended; null if there was none.</summary>
    ExportState? Export { get; }

    /// <summary>Cancels the running export; false if none is running.</summary>
    bool CancelExport();

    /// <summary>Moves the playhead (and the player).</summary>
    void Seek(double time);

    /// <summary>Selects a clip in the timeline and the clip list; null clears the selection.</summary>
    void SelectClip(int? clipId);

    void SetPlaying(bool playing);

    /// <summary>
    /// Opens a video or a .highlightcut.json (or .ourcut.json) project. May first wait for the user to allow it. Returns why it failed or
    /// why the user declined, or null.
    /// </summary>
    Task<string?> OpenAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the project (to <paramref name="path"/> if given). May first wait for the user to allow it. Returns why
    /// it failed or why the user declined, or null.
    /// </summary>
    Task<string?> SaveAsync(string? path, CancellationToken cancellationToken);
}

/// <summary>Silences found in the source's audio.</summary>
/// <param name="ThresholdDb">Peak level (dBFS) below which audio counted as silent.</param>
/// <param name="NoiseFloorDb">Level of the quietest 5 % of the audio: roughly the background noise.</param>
/// <param name="IsComplete">False while the audio is still being analysed.</param>
public sealed record SilenceReport(IReadOnlyList<TimeRange> Ranges, double ThresholdDb, double NoiseFloorDb, bool IsComplete);

/// <summary>Scene changes found in the source's video.</summary>
/// <param name="IsComplete">False while detection is still running; <paramref name="Times"/> covers the part scanned.</param>
/// <param name="Progress">Part of the video scanned, 0..1.</param>
public sealed record SceneReport(IReadOnlyList<double> Times, bool IsComplete, double Progress);

/// <summary>Export choices; null keeps what the Export dialog has.</summary>
/// <param name="Mode"><c>lossless</c> or <c>reencode</c>.</param>
/// <param name="Container"><c>mp4</c>, <c>mov</c> or <c>mkv</c>.</param>
/// <param name="Folder">Full path of the output folder.</param>
/// <param name="AllTracks">Keep every audio and subtitle track, or only the audio tracks not muted in the editor.</param>
/// <param name="Video"><c>h264</c>, <c>h264_fast</c> or <c>h265</c> (re-encoding).</param>
/// <param name="Audio"><c>copy</c> or <c>aac</c> (re-encoding).</param>
public sealed record ExportRequest(string? Mode = null, string? Container = null, bool? Merge = null, string? Folder = null,
    bool? Chapters = null, bool? AllTracks = null, string? Video = null, string? Audio = null);

/// <summary>An export as Claude sees it.</summary>
/// <param name="Status"><c>running</c>, <c>done</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="Progress">0..1.</param>
/// <param name="Files">The files it writes (while running) or wrote.</param>
/// <param name="Settings">e.g. "Lossless copy · MP4 · merged".</param>
public sealed record ExportState(string Status, double Progress, IReadOnlyList<string> Files, string? Error, string Settings);

/// <summary>The transcript of the open video.</summary>
/// <param name="State"><c>none</c> (not started), <c>waiting</c> (for the rest of the analysis), <c>running</c>, <c>done</c> or <c>failed</c>.</param>
/// <param name="Progress">Part of the audio transcribed, 0..1.</param>
/// <param name="Transcript">What has been transcribed so far; null before it starts.</param>
public sealed record TranscriptStatus(string State, double Progress, Transcript? Transcript, string? Error);
