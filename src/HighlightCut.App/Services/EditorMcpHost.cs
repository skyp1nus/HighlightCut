using Avalonia.Threading;
using HighlightCut.App.ViewModels;
using HighlightCut.Core.Editing;
using HighlightCut.Media.Export;
using HighlightCut.Mcp;
using Fillers = HighlightCut.Core.Transcripts.FillerWords;

namespace HighlightCut.App.Services;

/// <summary>
/// The editor as the MCP tools see it. Tool calls arrive on pipe threads; each one runs on the UI
/// thread, where the session and the view models live, so an edit by Claude never lands in the
/// middle of one by the user. Claude's edits then show up like the user's (and highlighted) in the
/// timeline, the clip list and the Claude panel.
/// </summary>
public sealed class EditorMcpHost(EditorViewModel editor) : IEditorHost, IEditorContext
{
    public Task<T> RunAsync<T>(Func<IEditorContext, Task<T>> action) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            editor.Claude.NoteActivity();
            return action(this);
        });

    public EditorSession Session => editor.Session;
    public bool HasFile => editor.HasFile;
    public string? SourceSummary => editor.HasFile ? editor.MediaInfoText : null;
    public string? ProjectPath => editor.ProjectPath;
    public double Playhead => editor.Time;
    public int? SelectedClipId => editor.SelectedClip?.Id;
    public bool IsPlaying => editor.IsPlaying;
    public IReadOnlyList<double> Keyframes => editor.Media?.Keyframes ?? [];

    /// <summary>Scans the keyframes if the Keyframes chip has not (they stay for the ticks and snapping once found).</summary>
    public Task<IReadOnlyList<double>> ReadKeyframesAsync(CancellationToken cancellationToken) =>
        editor.Media?.ReadKeyframesAsync(cancellationToken) ?? Task.FromResult<IReadOnlyList<double>>([]);

    /// <summary>Reads the audio if neither the Waveform nor the Silence chip has.</summary>
    public Task ReadAudioAsync(CancellationToken cancellationToken) =>
        editor.Media?.ReadWaveformAsync(cancellationToken) ?? Task.CompletedTask;

    public SilenceReport? FindSilences(double minDuration, double? thresholdDb, IReadOnlyList<int>? streams) =>
        editor.Media?.FindSilences(minDuration, thresholdDb, streams) is { } found
            ? new SilenceReport(found.Ranges, found.ThresholdDb, found.NoiseFloorDb, found.IsComplete)
            : null;

    /// <summary>Starts scene detection the first time Claude asks; later calls see how far it got.</summary>
    public SceneReport? FindSceneChanges(double threshold)
    {
        editor.Media?.DetectScenes();
        return editor.Media?.FindSceneChanges(threshold) is { } found
            ? new SceneReport(found.Changes, found.IsComplete, found.Progress)
            : null;
    }

    public string? AnalysisStatus => editor.Media?.Activity;

    /// <summary>Settings → Transcription → Filler words, every language.</summary>
    public IReadOnlyList<string> FillerWords => Fillers.All(editor.Settings.FillerWords);

    public TranscriptStatus TranscriptStatus => editor.Media is { } media
        ? new TranscriptStatus(media.TranscriptState.ToString().ToLowerInvariant(), media.TranscriptProgress, media.Transcript, media.TranscriptError)
        : new TranscriptStatus("none", 0, null, null);

    public string? StartTranscription() => editor.StartTranscription();

    public async Task<string?> StartExportAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        var export = editor.Export;
        var claude = editor.Claude.Export;
        if (editor.Claude.IsAsking)
            return AlreadyAsking;
        if (editor.Settings.ExportPermission == McpPermission.Never)
            return "The user exports by themselves (Settings → MCP server → Export: Never). Tell them the cut is ready to export.";
        if (export.PrepareForClaude(
                request.Mode switch { "lossless" => ExportMode.Copy, "reencode" => ExportMode.Encode, _ => null },
                request.Container?.ToUpperInvariant(),
                request.Merge,
                request.Folder,
                request.Chapters,
                request.AllTracks,
                request.Video switch
                {
                    "h264" => VideoEncoding.H264Quality,
                    "h264_fast" => VideoEncoding.H264Fast,
                    "h265" => VideoEncoding.H265,
                    _ => null,
                },
                request.Audio is null ? null : request.Audio == "copy") is { } error)
            return error;
        bool started = false;
        try
        {
            // Said before the user is asked: the videos cannot be joined without re-encoding.
            if (export.LosslessReason is { } reason)
                return reason + " Export with mode reencode to join them, or with merge false for separate files.";
            if (await AskToExportAsync(export.ClaudeTarget(), cancellationToken).ConfigureAwait(true) == ClaudeExportAnswer.Deny)
                return "The user declined the export.";
            if (export.StartPreparedForClaude() is { } refused)
            {
                claude.Clear();
                return refused;
            }
            started = true;
            return null;
        }
        catch (OperationCanceledException)
        {
            return "The export request was withdrawn.";
        }
        finally
        {
            if (!started)
                export.AbandonPreparedForClaude();
        }
    }

    /// <summary>Settings → MCP server → Export: Allow starts right away, Ask shows the request first.</summary>
    private Task<ClaudeExportAnswer> AskToExportAsync(ClaudeExportTarget target, CancellationToken cancellationToken) =>
        editor.Settings.ExportPermission == McpPermission.Allow
            ? Task.FromResult(ClaudeExportAnswer.Allow)
            : editor.Claude.Export.AskAsync(target, cancellationToken);

    private const string AlreadyAsking = "Another request is waiting for the user's answer; try again once they have answered.";

    public ExportState? Export => editor.Export switch
    {
        { Outcome: ExportOutcome.None } => null,
        var e => new ExportState(e.Outcome.ToString().ToLowerInvariant(), e.Outcome == ExportOutcome.Done ? 1 : e.Progress,
            e.OutputFiles, e.Outcome == ExportOutcome.Failed ? e.ErrorText : null, e.Footer),
    };

    public bool CancelExport()
    {
        if (editor.Export.Outcome != ExportOutcome.Running)
            return false;
        editor.Export.CancelExport();
        return true;
    }

    public void Seek(double time) => editor.SetTime(time);

    public void SelectClip(int? clipId) => editor.Select(clipId is { } id ? editor.Find(id) : null);

    public void SetPlaying(bool playing)
    {
        if (playing != editor.IsPlaying)
            editor.TogglePlay();
    }

    public async Task<string?> OpenAsync(string path, CancellationToken cancellationToken) =>
        await PermitAsync(editor.Settings.OpenFilesPermission, files => files.AskToOpenAsync(path, cancellationToken),
            $"The user declined opening {Path.GetFileName(path)}.").ConfigureAwait(true)
        ?? await editor.OpenForClaudeAsync(path).ConfigureAwait(true);

    /// <summary>Adds a video like Add video… does, after asking as for opening a file (Settings → MCP server → Open files).</summary>
    public async Task<string?> AddVideoAsync(string path, CancellationToken cancellationToken) =>
        await PermitAsync(editor.Settings.OpenFilesPermission, files => files.AskToAddAsync(path, cancellationToken),
            $"The user declined adding {Path.GetFileName(path)}.").ConfigureAwait(true)
        ?? await editor.AddVideoForClaudeAsync(path).ConfigureAwait(true);

    public async Task<string?> SaveAsync(string? path, CancellationToken cancellationToken)
    {
        if (editor.SavePathForClaude(path, out string? error) is not { } full)
            return error;
        return await PermitAsync(editor.Settings.SaveProjectPermission, files => files.AskToSaveAsync(full, cancellationToken),
                "The user declined saving the project.").ConfigureAwait(true)
            ?? await editor.SaveToAsync(full, auto: false).ConfigureAwait(true);
    }

    /// <summary>
    /// Settings → MCP server → Open files or Save project: Allow goes ahead, Ask shows the request first. Returns why
    /// not (declined, withdrawn, another request waiting), or null to go ahead.
    /// </summary>
    private async Task<string?> PermitAsync(McpPermission permission, Func<ClaudeFileRequestViewModel, Task<bool>> ask, string declined)
    {
        if (permission == McpPermission.Allow)
            return null;
        if (editor.Claude.IsAsking)
            return AlreadyAsking;
        try
        {
            return await ask(editor.Claude.Files).ConfigureAwait(true) ? null : declined;
        }
        catch (OperationCanceledException)
        {
            return "The request was withdrawn.";
        }
    }
}
