using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using HighlightCut.Core.Editing;
using HighlightCut.Core.Editing.Commands;
using HighlightCut.Core.Model;
using HighlightCut.Core.Transcripts;
using HighlightCut.Core.Time;

namespace HighlightCut.Mcp;

public sealed record ClipInfo(
    int Id,
    [property: Description("1-based position in the output.")] int Position,
    string Label,
    [property: Description("Start in seconds on the timeline (the videos end to end).")] double Start,
    [property: Description("End in seconds on the timeline.")] double End,
    double Duration,
    [property: Description("False for excluded clips: kept in the project, left out of the export.")] bool Included,
    [property: Description("Start–end as MM:SS.mmm, as the editor shows it.")] string Range,
    [property: Description("Colour of the clip in the editor: teal, amber, violet, rose, lime, cyan, orange, indigo, emerald or pink.")]
    string Color,
    [property: Description("Id of the video the clip is cut from (see sources). A clip never runs into the next video.")] int Source);

public sealed record AudioTrackInfo(
    int Track,
    string Label,
    [property: Description("Volume set in the editor, in dB: 0 is unchanged, -40 silent. It applies to the preview and the export.")] double VolumeDb,
    [property: Description("Muted in the editor: not heard in the preview, and left out of exports that keep only unmuted tracks.")] bool Muted);

public sealed record SourceInfo(
    [property: Description("Stable id of the video in the project; clips name their video by it.")] int Id,
    string Path,
    [property: Description("The file name.")] string Name,
    string Summary,
    double Duration,
    [property: Description("Where the video starts on the timeline, in seconds: the length of the videos before it.")] double Offset,
    double FrameRate,
    IReadOnlyList<AudioTrackInfo> AudioTracks);

public sealed record ProjectInfo(
    string Name,
    [property: Description("The first video (the only one unless the project has several); null when none is open.")] SourceInfo? Source,
    [property: Description("Where the project is saved, if it is.")] string? ProjectFile,
    double Playhead,
    int? SelectedClip,
    bool Playing,
    [property: Description("Total length of the included clips, in seconds.")] double OutputDuration,
    [property: Description("In output order.")] IReadOnlyList<ClipInfo> Clips,
    [property: Description("Background analysis still running, e.g. \"analysing 45%\".")] string? Analysis,
    [property: Description("Transcript: done, transcribing 34%, not started (and why)…; read it with get_transcript.")] string? Transcript = null,
    [property: Description("Every video of the project in timeline order, end to end on one timeline.")] IReadOnlyList<SourceInfo>? Sources = null,
    [property: Description("Length of the timeline in seconds: every video end to end.")] double? TimelineDuration = null);

public sealed record EditResult(
    [property: Description("Id of the edit in the history (for revert_action); null if nothing changed.")] long? Action,
    string Result,
    IReadOnlyList<ClipInfo> Clips,
    double OutputDuration);

public sealed record HistoryItem(long Id, string Description, [property: Description("\"user\" or \"claude\".")] string By, string Time,
    [property: Description("False when undone.")] bool Applied, bool Reverted);

public sealed record VideoFile(string Path, string Name, long SizeBytes, string Modified);

public sealed record ExportResult(
    [property: Description("running, done, failed or cancelled.")] string Status,
    [property: Description("0..1.")] double Progress,
    [property: Description("Files the export writes (while running) or wrote.")] IReadOnlyList<string> Files,
    string? Error,
    [property: Description("Mode, container and whether the clips are merged, e.g. \"Lossless copy · MP4 · merged\".")] string Settings,
    string? Note);

public sealed record TranscriptWordInfo(string Text, double Start, double End);

public sealed record TranscriptResult(
    [property: Description("done, or how far transcription is, e.g. \"transcribing 34%\".")] string Status,
    string? Model,
    [property: Description("True when word times are estimated (Whisper models): good to about half a second.")] bool ApproximateTimes,
    [property: Description("One per sentence or phrase: \"MM:SS.mmm–MM:SS.mmm text\".")] IReadOnlyList<string> Lines,
    [property: Description("Every word with its time, when asked for.")] IReadOnlyList<TranscriptWordInfo>? Words,
    [property: Description("More follows: call again with start = nextStart.")] double? NextStart,
    string? Note);

public sealed record TranscriptMatch(
    double Start,
    double End,
    [property: Description("Start–end as MM:SS.mmm.")] string Range,
    [property: Description("The words found.")] string Text,
    [property: Description("The sentence around them.")] string Context);

public sealed record MatchesResult(
    IReadOnlyList<TranscriptMatch> Matches,
    int Count,
    [property: Description("Seconds the matches take together.")] double Total,
    [property: Description("done, or how far transcription is: only the part done so far was searched.")] string Status,
    string? Note);

/// <summary>A timeline range to cut.</summary>
public sealed record CutRange([property: Description("Seconds on the timeline.")] double Start, [property: Description("Seconds on the timeline.")] double End);

public sealed record SilenceInfo(double Start, double End, double Duration,
    [property: Description("Start–end as MM:SS.mmm, as the editor shows it.")] string Range);

public sealed record SilencesResult(
    IReadOnlyList<SilenceInfo> Silences,
    int Count,
    [property: Description("Total silence, in seconds.")] double Total,
    [property: Description("Peak level (dBFS) below which audio counted as silent.")] double ThresholdDb,
    [property: Description("Level of the quietest 5 % of the audio: roughly the background noise.")] double NoiseFloorDb,
    [property: Description("False if not all of the audio could be read; the silences found in the part read are listed.")] bool Complete,
    string? Note);

public sealed record ScenesResult(
    [property: Description("Seconds on the timeline where a new scene starts.")] IReadOnlyList<double> Changes,
    int Count,
    double Threshold,
    [property: Description("False while detection is still running; the changes found so far are listed.")] bool Complete,
    string? Note);

/// <summary>One step of <c>edit_timeline</c>.</summary>
public sealed record EditOperation(
    [property: Description("add, remove, trim, split, join, include, exclude, move, rename or color.")] string Action,
    [property: Description("Clip id (all actions except add); for join, the first of the two clips.")] int? Clip = null,
    [property: Description("Timeline seconds: the range for add (one clip per video if it crosses a join), a new start for trim.")] double? Start = null,
    [property: Description("Timeline seconds: the range for add, a new end for trim.")] double? End = null,
    [property: Description("Timeline seconds: where to split.")] double? Time = null,
    [property: Description("Label for add or rename. Clip names are unique: a taken name is refused on rename and gets \" · 2\" added on add.")]
    string? Label = null,
    [property: Description("1-based output position for add or move.")] int? Position = null,
    [property: Description("Colour for add or color: teal, amber, violet, rose, lime, cyan, orange, indigo, emerald or pink.")]
    string? Color = null);

/// <summary>
/// The MCP tools: reading the project and editing it through HighlightCut.Core's commands, so every edit
/// Claude makes is undoable and shows up (highlighted) in the editor's Claude panel.
/// </summary>
public sealed class EditorTools(IEditorHost host)
{
    public const string ServerName = "highlightcut";

    public const string Instructions = """
        HighlightCut is a video editor for cutting long recordings down to the parts worth keeping. A project has
        one video, or several end to end on one timeline (get_project lists them as sources, with where each starts).
        Clips are ranges of the timeline in seconds, the same times the user sees; a clip belongs to one video and
        never runs into the next, so a range over the join between two videos becomes one clip per video. The
        order of the clips in the project is the order of the output. Parts of the timeline not covered by an
        included clip are not exported; excluded clips stay in the project but are not exported either.

        Start with get_project. Times are timeline seconds (decimals allowed); with one video they are seconds on
        that video. find_keyframes, find_silences, find_scene_changes and the transcript tools cover every video and
        give timeline seconds too. Clip ids are stable; positions are 1-based output positions. Lossless export starts each clip
        at the keyframe at or before its start; use find_keyframes when exact starts matter.

        You cannot see or hear the video, but find_silences shows where the speaker pauses (the first call reads the
        audio, which takes a moment on a long video) and find_scene_changes where the picture changes (a cut, a new slide or window). Scene
        detection reads every frame, so it starts the first time you ask and fills in: call again for the rest.
        cut_silences removes pauses in one step.

        get_transcript gives what is said, as timed sentences (HighlightCut transcribes locally the first time it is
        asked, once a model is installed in Settings → Transcription); search_transcript finds words or phrases, find_filler_words the
        user's filler words (ums, uhs…). Use the times to add, trim or split clips, label clips after what is said, or cut words and
        sentences out with cut_ranges or cut_filler_words.

        Everything you change appears in HighlightCut's Claude panel, highlighted, and the user can undo it. For
        several related changes use edit_timeline with a short description, so they form one undo step.
        When the cut is ready, export writes it out (lossless and next to the video unless told otherwise).
        """;

    private static readonly string[] VideoExtensions =
        [".mp4", ".mov", ".mkv", ".webm", ".m4v", ".avi", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".flv", ".wmv"];

    // Tool results are read by Claude, not put into HTML: keep "·", "–" and non-English labels as they are.
    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>All tools, bound to <paramref name="host"/>.</summary>
    public static IReadOnlyList<McpServerTool> Create(IEditorHost host)
    {
        var tools = new EditorTools(host);
        return [.. typeof(EditorTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => McpServerTool.Create(m, m.IsStatic ? null : tools, new McpServerToolCreateOptions { SerializerOptions = Json }))];
    }

    // ---- Reading -------------------------------------------------------------------------

    [McpServerTool(Name = "get_project", Title = "Get the project", ReadOnly = true, Idempotent = true)]
    [Description("The videos (sources, in timeline order, with where each starts on the timeline), the playhead and every clip " +
                 "in output order with its timeline range and video. Call this before editing.")]
    public Task<ProjectInfo> GetProject() => host.RunAsync(ctx => Task.FromResult(Describe(ctx)));

    [McpServerTool(Name = "get_history", Title = "List recent edits", ReadOnly = true, Idempotent = true)]
    [Description("Recent edits, newest first, by the user and by you. Ids work with revert_action.")]
    public Task<IReadOnlyList<HistoryItem>> GetHistory([Description("How many edits (default 30).")] int limit = 30) =>
        host.RunAsync(ctx =>
        {
            var session = ctx.Session;
            IReadOnlyList<HistoryItem> items = [.. session.History.Entries.Reverse().Take(Math.Clamp(limit, 1, 500))
                .Select(e => new HistoryItem(e.Id, e.Description, e.Origin == EditOrigin.Assistant ? "claude" : "user",
                    e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture), session.History.IsApplied(e), session.IsReverted(e)))];
            return Task.FromResult(items);
        });

    [McpServerTool(Name = "find_keyframes", Title = "Find keyframes", ReadOnly = true, Idempotent = true)]
    [Description("Keyframe times between two points of the timeline (every video's). A lossless export starts each clip at the keyframe at or " +
                 "before its start, so starting clips on keyframes avoids extra lead-in. The first call may take a moment " +
                 "while the video is scanned.")]
    public Task<IReadOnlyList<double>> FindKeyframes([Description("Seconds.")] double start, [Description("Seconds.")] double end,
        CancellationToken cancellationToken = default) =>
        host.RunAsync(async ctx =>
        {
            RequireFile(ctx);
            var keyframes = await ctx.ReadKeyframesAsync(cancellationToken).ConfigureAwait(true);
            if (keyframes.Count == 0)
                throw new McpException("This file has no keyframes to cut on (no video, or the scan failed).");
            IReadOnlyList<double> times = [.. keyframes.Where(t => t >= start && t <= end).Take(500).Select(Round)];
            return times;
        });

    [McpServerTool(Name = "list_videos", Title = "List recent videos", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Video files in a folder (default: the user's Videos folder) and its subfolders, newest first. " +
                 "Use a path from here with open_file.")]
    public static IReadOnlyList<VideoFile> ListVideos(
        [Description("Folder to search; the user's Videos folder if omitted.")] string? folder = null,
        [Description("How many files (default 20).")] int limit = 20)
    {
        folder ??= DefaultVideosFolder();
        RequireFullPath(folder);
        if (!Directory.Exists(folder))
            throw new McpException($"The folder {folder} does not exist.");
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        return [.. new DirectoryInfo(folder).EnumerateFiles("*", options)
            .Where(f => VideoExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(f => new VideoFile(f.FullName, f.Name, f.Length, f.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))];
    }

    [McpServerTool(Name = "find_silences", Title = "Find silences", ReadOnly = true, Idempotent = true)]
    [Description("Pauses where every audio track (or the given tracks) stays quiet. The level follows the recording's " +
                 "background noise unless you give one. cut_silences removes them. The first call may take a moment while " +
                 "the audio is read.")]
    public Task<SilencesResult> FindSilences(
        [Description("Shortest pause, in seconds (default 1).")] double minDuration = 1.0,
        [Description("Peak level in dBFS that counts as silent, e.g. -40; automatic if omitted.")] double? thresholdDb = null,
        [Description("Audio track numbers (as in get_project; track N of each video) that must be quiet; all if omitted.")] IReadOnlyList<int>? tracks = null,
        [Description("Only pauses that end after this time, in seconds.")] double? start = null,
        [Description("Only pauses that begin before this time, in seconds.")] double? end = null,
        CancellationToken cancellationToken = default) =>
        host.RunAsync(async ctx =>
        {
            var report = await SilencesAsync(ctx, minDuration, thresholdDb, tracks, cancellationToken).ConfigureAwait(true);
            var ranges = report.Ranges.Where(r => r.End > (start ?? double.MinValue) && r.Start < (end ?? double.MaxValue)).ToList();
            const int Max = 500;
            string? note = !report.IsComplete ? "Not all of the audio could be read" + (ctx.AnalysisStatus is { } a ? $" ({a})" : "") + "; these are the pauses in the part that was."
                : ranges.Count > Max ? $"Only the first {Max} are listed; narrow the range or raise minDuration."
                : ranges.Count == 0 ? $"No pauses of {minDuration:0.##} s or more below {report.ThresholdDb:0.#} dBFS." : null;
            return new SilencesResult(
                [.. ranges.Take(Max).Select(r => new SilenceInfo(Round(r.Start), Round(r.End), Round(r.End - r.Start), RangeText(r.Start, r.End)))],
                ranges.Count, Round(ranges.Sum(r => r.End - r.Start)), report.ThresholdDb, report.NoiseFloorDb, report.IsComplete, note);
        });

    [McpServerTool(Name = "find_scene_changes", Title = "Find scene changes", ReadOnly = true, Idempotent = true)]
    [Description("Times where the picture changes abruptly: a camera cut, a new slide, switching windows. Steady motion " +
                 "(panning, scrolling) does not count.")]
    public Task<ScenesResult> FindSceneChanges(
        [Description("Sensitivity on ffmpeg scdet's 0–100 scale (default 10); lower finds more and subtler changes.")] double threshold = 10,
        [Description("Only changes at or after this time, in seconds.")] double? start = null,
        [Description("Only changes before this time, in seconds.")] double? end = null) =>
        host.RunAsync(ctx =>
        {
            RequireFile(ctx);
            if (threshold is <= 0 or > 100)
                throw new McpException("The threshold goes from 0 (exclusive) to 100.");
            var report = ctx.FindSceneChanges(threshold) ?? throw new McpException("This file has no video.");
            var times = report.Times.Where(t => t >= (start ?? double.MinValue) && t < (end ?? double.MaxValue)).Select(Round).ToList();
            string? note = !report.IsComplete
                ? $"Scene detection is {Math.Floor(report.Progress * 100):0}% done; call again for the rest."
                : times.Count == 0 ? "No scene changes at this sensitivity; a lower threshold finds subtler ones." : null;
            return Task.FromResult(new ScenesResult(times, times.Count, threshold, report.IsComplete, note));
        });

    // ---- Editing -------------------------------------------------------------------------

    [McpServerTool(Name = "add_segment", Title = "Keep a range")]
    [Description("Keeps a range of the timeline as a new clip. Returns the updated clip list; the new clip is the one with the highest id. " +
                 "A range over the join between two videos becomes one clip per video, next to each other in the output. " +
                 "Clips cannot share source time (it would be exported twice): a range that overlaps another clip is refused.")]
    public Task<EditResult> AddSegment(
        [Description("Timeline seconds.")] double start,
        [Description("Timeline seconds.")] double end,
        [Description("Short name shown in the clip list; \"Clip N\" if omitted. A name another clip has gets \" · 2\" added.")]
        string? label = null,
        [Description("1-based output position; the end if omitted.")] int? position = null,
        [Description("teal, amber, violet, rose, lime, cyan, orange, indigo, emerald or pink; by default the next colour its neighbours do not have.")]
        string? color = null) =>
        Edit(_ => new AddRangeCommand(start, end, label, position - 1, color is null ? null : ParseColor(color)));

    [McpServerTool(Name = "remove_segment", Title = "Remove a clip", Destructive = true)]
    [Description("Removes a clip from the project. To only leave it out of the export, use set_included instead.")]
    public Task<EditResult> RemoveSegment(int clip) => Edit(_ => new RemoveClipCommand(clip));

    [McpServerTool(Name = "trim_segment", Title = "Trim a clip")]
    [Description("Moves a clip's start and/or end. A clip cannot grow into another clip or past the ends of its video; to make " +
                 "two clips one, use join_segments.")]
    public Task<EditResult> TrimSegment(int clip, [Description("New start, timeline seconds.")] double? start = null,
        [Description("New end, timeline seconds.")] double? end = null) =>
        Edit(_ => new PartialTrimCommand(clip, start, end));

    [McpServerTool(Name = "split_segment", Title = "Split a clip")]
    [Description("Splits a clip in two at a timeline time; the second part gets a new id.")]
    public Task<EditResult> SplitSegment(int clip, [Description("Timeline seconds, inside the clip.")] double time) =>
        Edit(_ => new SplitAtCommand(clip, time));

    [McpServerTool(Name = "join_segments", Title = "Join two clips")]
    [Description("Joins a clip with the clip right after it in its video into one clip, which keeps the first clip's " +
                 "id, label and inclusion. The two must be in the same video, touch or be less than 0.5 s apart (the gap is " +
                 "kept too) and be next to each other in the output.")]
    public Task<EditResult> JoinSegments([Description("The first of the two clips.")] int clip) =>
        Edit(project => JoinClipsCommand.WithNext(project, clip));

    [McpServerTool(Name = "set_included", Title = "Exclude or keep a clip")]
    [Description("Excludes a clip from the export (it stays in the project) or includes it again.")]
    public Task<EditResult> SetIncluded(int clip, bool included) => Edit(_ => new SetClipIncludedCommand(clip, included));

    [McpServerTool(Name = "move_segment", Title = "Reorder a clip")]
    [Description("Moves a clip to another position in the output.")]
    public Task<EditResult> MoveSegment(int clip, [Description("1-based output position.")] int position) =>
        Edit(_ => new MoveClipCommand(clip, position - 1));

    [McpServerTool(Name = "set_label", Title = "Rename a clip")]
    [Description("Renames a clip. Labels become chapter titles and file names on export. Clip names are unique: a name another " +
                 "clip has is refused.")]
    public Task<EditResult> SetLabel(int clip, string label) => Edit(_ => new RenameClipCommand(clip, label));

    [McpServerTool(Name = "set_color", Title = "Colour a clip")]
    [Description("Sets the colour a clip has on the timeline and in the clip list, e.g. to group related clips.")]
    public Task<EditResult> SetColor(int clip,
        [Description("teal, amber, violet, rose, lime, cyan, orange, indigo, emerald or pink.")] string color) =>
        Edit(_ => new SetClipColorCommand(clip, ParseColor(color)));

    [McpServerTool(Name = "edit_timeline", Title = "Make several edits")]
    [Description("Applies several edits in order as one undo step, e.g. keeping a list of ranges or excluding several clips. " +
                 "If one fails, none is applied.")]
    public Task<EditResult> EditTimeline(
        IReadOnlyList<EditOperation> operations,
        [Description("What the edits do, in a few words; shown to the user, e.g. \"Kept the three demo segments\".")] string description) =>
        Edit(_ =>
        {
            if (operations.Count == 0)
                throw new EditException("No operations given.");
            return new BatchCommand("edit_timeline", string.IsNullOrWhiteSpace(description) ? $"{operations.Count} edits" : description.Trim(),
                [.. operations.Select(ToCommand)]);
        });

    [McpServerTool(Name = "cut_silences", Title = "Cut out silences")]
    [Description("Cuts pauses out of the included clips (or the given clips) as one undo step, keeping a little of each " +
                 "pause so speech does not sound clipped. With no clips yet, it first keeps every video whole. Lossless export " +
                 "starts each clip at the keyframe before it, so many short clips are best exported with re-encoding.")]
    public Task<EditResult> CutSilences(
        [Description("Shortest pause to cut, in seconds (default 1).")] double minDuration = 1.0,
        [Description("Peak level in dBFS that counts as silent; automatic if omitted (see find_silences).")] double? thresholdDb = null,
        [Description("Seconds of each pause to keep on both sides (default 0.15).")] double padding = 0.15,
        [Description("Clip ids to cut; every included clip if omitted.")] IReadOnlyList<int>? clips = null,
        [Description("Audio track numbers that must be quiet; all if omitted.")] IReadOnlyList<int>? tracks = null,
        CancellationToken cancellationToken = default) =>
        host.RunAsync(async ctx =>
        {
            if (padding < 0)
                throw new McpException("The padding cannot be negative.");
            var report = await SilencesAsync(ctx, minDuration, thresholdDb, tracks, cancellationToken).ConfigureAwait(true);
            if (!report.IsComplete)
                throw new McpException("Not all of the audio could be read" + (ctx.AnalysisStatus is { } a ? $" ({a})" : "") + ", so nothing was cut.");
            var cuts = report.Ranges.Select(r => new TimeRange(r.Start + padding, r.End - padding))
                .Where(r => r.End - r.Start >= 0.05).ToList();
            return CutOut(ctx, cuts, clips, "cut_silences",
                n => $"Removed {n} silence{(n == 1 ? "" : "s")}",
                $"No pauses of {minDuration:0.##} s or more below {report.ThresholdDb:0.#} dBFS in those clips; nothing changed.");
        });

    [McpServerTool(Name = "cut_ranges", Title = "Cut out ranges")]
    [Description("Cuts timeline ranges (e.g. words or sentences found in the transcript) out of the included clips, or the " +
                 "given clips, as one undo step, trimming or splitting the clips they touch. With no clips yet, it first keeps " +
                 "every video whole.")]
    public Task<EditResult> CutRanges(
        [Description("Ranges of the timeline to remove, in seconds.")] IReadOnlyList<CutRange> ranges,
        [Description("What the cut removes, in a few words; shown to the user, e.g. \"Removed the false start\".")] string description,
        [Description("Clip ids to cut; every included clip if omitted.")] IReadOnlyList<int>? clips = null) =>
        host.RunAsync(ctx =>
        {
            RequireFile(ctx);
            if (ranges.Count == 0)
                throw new McpException("Give at least one range.");
            if (ranges.Any(r => !double.IsFinite(r.Start) || !double.IsFinite(r.End) || r.End <= r.Start))
                throw new McpException("Each range needs a start before its end, in seconds.");
            string text = string.IsNullOrWhiteSpace(description) ? $"Cut {ranges.Count} ranges" : description.Trim();
            return Task.FromResult(CutOut(ctx, [.. ranges.Select(r => new TimeRange(r.Start, r.End))], clips, "cut_ranges",
                _ => text, "None of the ranges is inside those clips; nothing changed."));
        });

    [McpServerTool(Name = "cut_filler_words", Title = "Cut out filler words")]
    [Description("Cuts the user's filler words (Settings → Transcription; by default um, uh, er, like, you know; е-е, ну, " +
                 "типу, короче) or the given words out of the included clips, or the given clips, as one undo step. Needs the " +
                 "finished transcript. Some fillers (like, you know) are also real words: find_filler_words first to check.")]
    public Task<EditResult> CutFillerWords(
        [Description("Words or short phrases to cut instead of the user's filler words.")] IReadOnlyList<string>? words = null,
        [Description("Seconds added before and after each word (default 0.02).")] double padding = 0.02,
        [Description("Clip ids to cut; every included clip if omitted.")] IReadOnlyList<int>? clips = null) =>
        host.RunAsync(ctx =>
        {
            var (status, transcript) = RequireTranscript(ctx);
            if (status.State != "done")
                throw new McpException($"The transcript is not finished ({StatusText(status)}). Try again when it is.");
            if (padding is < 0 or > 1)
                throw new McpException("The padding goes from 0 to 1 second.");
            var matches = FindAll(transcript, words ?? ctx.FillerWords);
            var cuts = matches.Select(m => new TimeRange(Math.Max(0, m.Start - padding), m.End + padding)).ToList();
            var result = CutOut(ctx, cuts, clips, "cut_filler_words",
                n => $"Removed {n} filler word{(n == 1 ? "" : "s")}",
                "No filler words in those clips; nothing changed.");
            return Task.FromResult(transcript.ApproximateTimes
                ? result with { Result = result.Result + " Word times are estimated with this model; check the cuts." }
                : result);
        });

    [McpServerTool(Name = "revert_action", Title = "Revert an edit")]
    [Description("Reverts one earlier edit (an id from get_history or an edit result) and keeps everything done after it.")]
    public Task<EditResult> RevertAction([Description("Edit id.")] long action) =>
        host.RunAsync(ctx =>
        {
            RequireFile(ctx);
            var entry = ctx.Session.History.Find(action) ?? throw new McpException($"There is no edit {action} in the history.");
            var reverted = Guard(() => ctx.Session.Revert(entry, EditOrigin.Assistant));
            return Task.FromResult(Result(ctx, reverted));
        });

    [McpServerTool(Name = "undo", Title = "Undo")]
    [Description("Undoes the last edit, whoever made it.")]
    public Task<EditResult> Undo() => UndoRedo(undo: true);

    [McpServerTool(Name = "redo", Title = "Redo")]
    [Description("Redoes the last undone edit.")]
    public Task<EditResult> Redo() => UndoRedo(undo: false);

    // ---- Player and files ----------------------------------------------------------------

    [McpServerTool(Name = "seek", Title = "Move the playhead")]
    [Description("Moves the playhead so the user sees that frame; with a clip, also selects it (and goes to its start if no time is given).")]
    public Task<string> Seek([Description("Timeline seconds.")] double? time = null, [Description("Clip id to select.")] int? clip = null) =>
        host.RunAsync(ctx =>
        {
            RequireFile(ctx);
            if (clip is { } id)
            {
                var c = ctx.Session.Project.Find(id) ?? throw new McpException($"Clip {id} does not exist.");
                ctx.SelectClip(id);
                time ??= ctx.Session.Project.TimelineRange(c).Start;
            }
            if (time is { } t)
                ctx.Seek(Math.Clamp(t, 0, ctx.Session.Project.TimelineDuration));
            return Task.FromResult($"Playhead at {TimeFormat.Timecode(ctx.Playhead)}.");
        });

    [McpServerTool(Name = "set_playing", Title = "Play or pause")]
    [Description("Starts or pauses playback in HighlightCut.")]
    public Task<string> SetPlaying(bool playing) =>
        host.RunAsync(ctx =>
        {
            RequireFile(ctx);
            ctx.SetPlaying(playing);
            return Task.FromResult(playing ? "Playing." : "Paused.");
        });

    [McpServerTool(Name = "open_file", Title = "Open a video or project", OpenWorld = true)]
    [Description("Opens a video (as a new, empty project) or a saved .highlightcut.json project (or an older .ourcut.json one) in HighlightCut, replacing what is open. " +
                 "The user may be asked first (Settings → MCP server → Open files); the call waits for their answer.")]
    public Task<ProjectInfo> OpenFile([Description("Full path of the file.")] string path, CancellationToken cancellationToken = default) =>
        host.RunAsync(async ctx =>
        {
            RequireFullPath(path);
            if (!File.Exists(path))
                throw new McpException($"{path} does not exist.");
            if (await ctx.OpenAsync(path, cancellationToken).ConfigureAwait(true) is { } error)
                throw new McpException(error);
            return Describe(ctx);
        });

    [McpServerTool(Name = "save_project", Title = "Save the project")]
    [Description("Saves the project as .highlightcut.json: where it was saved before, or to the given path. The user may be asked " +
                 "first (Settings → MCP server → Save project); the call waits for their answer.")]
    public Task<string> SaveProject([Description("Full path ending in .highlightcut.json; needed the first time.")] string? path = null,
        CancellationToken cancellationToken = default) =>
        host.RunAsync(async ctx =>
        {
            RequireFile(ctx);
            if (path is null && ctx.ProjectPath is null)
                throw new McpException("The project has not been saved yet; give a path ending in .highlightcut.json.");
            if (path is not null)
                RequireFullPath(path);
            if (await ctx.SaveAsync(path, cancellationToken).ConfigureAwait(true) is { } error)
                throw new McpException(error);
            return $"Saved to {ctx.ProjectPath}.";
        });

    // ---- Transcript ----------------------------------------------------------------------

    [McpServerTool(Name = "get_transcript", Title = "Read the transcript", ReadOnly = true)]
    [Description("What is said in the video, as timed lines, one per sentence or phrase: \"MM:SS.mmm–MM:SS.mmm text\". " +
                 "HighlightCut transcribes after the video is opened (and starts now if it has not); while it runs, the part done so " +
                 "far is returned. A long transcript comes in parts: call again with start = nextStart.")]
    public Task<TranscriptResult> GetTranscript(
        [Description("Seconds; from the beginning if omitted.")] double? start = null,
        [Description("Seconds; to the end if omitted.")] double? end = null,
        [Description("Also list every word with its time (for exact cuts).")] bool words = false) =>
        host.RunAsync(ctx =>
        {
            var (status, transcript) = RequireTranscript(ctx);
            double from = start ?? 0, to = end ?? double.MaxValue;
            double? next = null;
            List<TranscriptWordInfo>? wordList = null;
            if (words)
            {
                const int MaxWords = 500;
                var inRange = transcript.Between(from, to).ToList();
                if (inRange.Count > MaxWords)
                {
                    next = inRange[MaxWords].Start;
                    inRange = inRange[..MaxWords];
                }
                wordList = [.. inRange.Select(w => new TranscriptWordInfo(w.Text, Round(w.Start), Round(w.End)))];
            }
            const int MaxChars = 20_000;
            var lines = new List<string>();
            int chars = 0;
            foreach (var phrase in transcript.Phrases())
            {
                if (phrase.End <= from || phrase.Start >= Math.Min(to, next ?? double.MaxValue))
                    continue;
                string line = $"{RangeText(phrase.Start, phrase.End)} {phrase.Text}";
                if (chars + line.Length > MaxChars)
                {
                    next = Math.Min(next ?? double.MaxValue, phrase.Start);
                    break;
                }
                lines.Add(line);
                chars += line.Length + 1;
            }
            string? note = status.State != "done" ? "Transcription is still running; call again later for the rest."
                : lines.Count == 0 ? "Nothing is said in this range." : null;
            return Task.FromResult(new TranscriptResult(StatusText(status), transcript.Model.Length > 0 ? transcript.Model : null,
                transcript.ApproximateTimes, lines, wordList, next is { } n ? Round(n) : null, note));
        });

    [McpServerTool(Name = "search_transcript", Title = "Search the transcript", ReadOnly = true)]
    [Description("Finds a word or phrase in the transcript (ignoring case and punctuation) and gives each place with its " +
                 "time and the sentence around it.")]
    public Task<MatchesResult> SearchTranscript(
        [Description("Word or phrase to find.")] string text,
        [Description("How many places at most (default 50).")] int limit = 50) =>
        host.RunAsync(ctx =>
        {
            var (status, transcript) = RequireTranscript(ctx);
            if (Tokens(text).Length == 0)
                throw new McpException("Give a word or phrase to find.");
            var matches = FindAll(transcript, [text]);
            return Task.FromResult(Matches(matches, limit, status,
                matches.Count == 0 ? $"“{text}” is not in the transcript{(status.State == "done" ? "" : " so far")}." : null));
        });

    [McpServerTool(Name = "find_filler_words", Title = "Find filler words", ReadOnly = true)]
    [Description("The user's filler words (Settings → Transcription; by default um, uh, er, like, you know; Ukrainian е-е, " +
                 "ну, типу, короче) with their times; or the words you give. " +
                 "Speech models often leave out ums and uhs, so short pauses (find_silences with a small minDuration) can " +
                 "show where they were.")]
    public Task<MatchesResult> FindFillerWords(
        [Description("Words or short phrases to find instead of the user's filler words.")] IReadOnlyList<string>? words = null,
        [Description("How many at most (default 200).")] int limit = 200) =>
        host.RunAsync(ctx =>
        {
            var (status, transcript) = RequireTranscript(ctx);
            var matches = FindAll(transcript, words ?? ctx.FillerWords);
            return Task.FromResult(Matches(matches, limit, status,
                matches.Count == 0 ? "No filler words in the transcript; speech models often leave them out." : null));
        });

    /// <summary>The transcript, starting transcription if it has not started (or failed before).</summary>
    private static (TranscriptStatus Status, Transcript Transcript) RequireTranscript(IEditorContext ctx)
    {
        RequireFile(ctx);
        var status = ctx.TranscriptStatus;
        if (status.State is "none" or "failed")
        {
            if (ctx.StartTranscription() is { } why)
                throw new McpException(status.State == "failed" ? $"Transcription failed: {status.Error}" : why);
            status = ctx.TranscriptStatus;
        }
        return (status, status.Transcript ?? new Transcript("", "auto", []));
    }

    private static string StatusText(TranscriptStatus status) => status.State switch
    {
        "done" => "done",
        "running" => $"transcribing {Math.Floor(status.Progress * 100).ToString(CultureInfo.InvariantCulture)}%",
        "waiting" => "waiting for the rest of the analysis",
        "failed" => "failed: " + status.Error,
        _ => "not started",
    };

    /// <summary>Lower-case words without surrounding punctuation.</summary>
    private static string[] Tokens(string text) =>
        [.. text.Split((char[])[' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().Trim(Punctuation).ToLowerInvariant())
            .Where(t => t.Length > 0)];

    private static readonly char[] Punctuation = ['.', ',', '!', '?', ';', ':', '"', '“', '”', '«', '»', '(', ')', '…', '—', '–', '\''];

    /// <summary>Every place one of <paramref name="phrases"/> is said, in time order.</summary>
    private static List<TranscriptMatch> FindAll(Transcript transcript, IEnumerable<string> phrases)
    {
        var words = transcript.Words;
        var normal = words.Select(w => Tokens(w.Text) is [var t, ..] ? t : "").ToArray();
        var phraseList = transcript.Phrases();
        var found = new List<TranscriptMatch>();
        var taken = new HashSet<int>();
        foreach (var query in phrases.Select(Tokens).Where(q => q.Length > 0).Distinct(new SequenceComparer()))
        {
            for (int i = 0; i + query.Length <= words.Count; i++)
            {
                if (taken.Contains(i) || !query.Select((t, k) => normal[i + k] == t).All(x => x))
                    continue;
                taken.Add(i);
                var first = words[i];
                var last = words[i + query.Length - 1];
                string context = phraseList.FirstOrDefault(p => i >= p.FirstWord && i < p.FirstWord + p.WordCount)?.Text ?? "";
                found.Add(new TranscriptMatch(Round(first.Start), Round(last.End), RangeText(first.Start, last.End),
                    string.Join(' ', words.Skip(i).Take(query.Length).Select(w => w.Text)), context));
            }
        }
        return [.. found.OrderBy(m => m.Start)];
    }

    private static MatchesResult Matches(List<TranscriptMatch> matches, int limit, TranscriptStatus status, string? note) =>
        new([.. matches.Take(Math.Clamp(limit, 1, 1000))], matches.Count, Round(matches.Sum(m => m.End - m.Start)), StatusText(status),
            note ?? (matches.Count > limit ? $"Only the first {limit} are listed." : null));

    private sealed class SequenceComparer : IEqualityComparer<string[]>
    {
        public bool Equals(string[]? x, string[]? y) => x is not null && y is not null && x.SequenceEqual(y);
        public int GetHashCode(string[] obj) => string.Join(' ', obj).GetHashCode(StringComparison.Ordinal);
    }

    // ---- Export --------------------------------------------------------------------------

    /// <summary>How long <c>export</c> waits for the export to finish before it reports progress instead.</summary>
    internal static TimeSpan ExportWait { get; set; } = TimeSpan.FromSeconds(20);

    private static readonly string[] Modes = ["lossless", "reencode"];
    private static readonly string[] Containers = ["mp4", "mov", "mkv"];
    private static readonly string[] VideoCodecs = ["h264", "h264_fast", "h265"];
    private static readonly string[] AudioCodecs = ["copy", "aac"];

    [McpServerTool(Name = "export", Title = "Export the video", OpenWorld = true)]
    [Description("Exports the included clips, like the Export button, with the progress shown in HighlightCut. Options you leave out " +
                 "keep what the Export dialog has, which starts from the user's Settings → Export. The user may be asked to allow " +
                 "the export first, and can decline. Existing files are never overwritten: a number is added to the name. " +
                 "Once it runs, waits up to 20 s; for a longer export, call get_export_status.")]
    public async Task<ExportResult> Export(
        [Description("lossless (stream copy, fast; each clip starts at the keyframe at or before its start) or reencode " +
                     "(frame-accurate, slower).")] string? mode = null,
        [Description("mp4, mov or mkv.")] string? container = null,
        [Description("true: one file with all included clips; false: one file per clip.")] bool? merge = null,
        [Description("Full path of the folder to write to; the Export dialog's folder if omitted.")] string? folder = null,
        [Description("A chapter per clip, named after it (merged files only).")] bool? chapters = null,
        [Description("true: every audio and subtitle track; false: only the audio tracks not muted in HighlightCut.")] bool? allTracks = null,
        [Description("Re-encoding: h264 (best quality), h264_fast or h265.")] string? video = null,
        [Description("Re-encoding: copy (keep the source audio) or aac.")] string? audio = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ExportRequest(Pick(mode, Modes, "mode"), Pick(container, Containers, "container"), merge, folder, chapters,
            allTracks, Pick(video, VideoCodecs, "video"), Pick(audio, AudioCodecs, "audio"));
        if (folder is not null)
            RequireFullPath(folder);
        var state = await host.RunAsync(async ctx =>
        {
            RequireFile(ctx);
            if (await ctx.StartExportAsync(request, cancellationToken).ConfigureAwait(true) is { } error)
                throw new McpException(error);
            return ctx.Export ?? throw new McpException("The export did not start.");
        }).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + ExportWait;
        while (state.Status == "running" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            state = await host.RunAsync(ctx => Task.FromResult(ctx.Export ?? state)).ConfigureAwait(false);
        }
        return Describe(state);
    }

    [McpServerTool(Name = "get_export_status", Title = "Check the export", ReadOnly = true, Idempotent = true)]
    [Description("Progress of the running export, or how the latest one ended (files written, or why it failed).")]
    public Task<ExportResult> GetExportStatus() =>
        host.RunAsync(ctx => Task.FromResult(Describe(ctx.Export ?? throw new McpException("Nothing has been exported yet."))));

    [McpServerTool(Name = "cancel_export", Title = "Cancel the export", Idempotent = true)]
    [Description("Stops the running export and removes its unfinished files.")]
    public Task<ExportResult> CancelExport() =>
        host.RunAsync(ctx =>
        {
            if (!ctx.CancelExport())
                throw new McpException("No export is running.");
            return Task.FromResult(Describe(ctx.Export!));
        });

    private static ExportResult Describe(ExportState state) =>
        new(state.Status, Math.Round(state.Progress, 3), state.Files, state.Error, state.Settings, state.Status switch
        {
            "running" => "Still exporting; call get_export_status to follow it.",
            "cancelled" => "The export was cancelled (by you or the user).",
            _ => null,
        });

    /// <summary>A choice in lower case, or null; an unknown one is refused with the valid ones listed.</summary>
    private static string? Pick(string? value, string[] allowed, string name)
    {
        if (value is null)
            return null;
        string v = value.Trim().ToLowerInvariant();
        return allowed.Contains(v) ? v : throw new McpException($"Unknown {name} “{value}”; use {string.Join(", ", allowed)}.");
    }

    // ---- Helpers -------------------------------------------------------------------------

    private Task<EditResult> Edit(Func<Project, IEditCommand> command) =>
        host.RunAsync(ctx =>
        {
            RequireFile(ctx);
            var entry = Guard(() => ctx.Session.Execute(command(ctx.Session.Project), EditOrigin.Assistant));
            return Task.FromResult(Result(ctx, entry));
        });

    private Task<EditResult> UndoRedo(bool undo) =>
        host.RunAsync(ctx =>
        {
            RequireFile(ctx);
            var history = ctx.Session.History;
            var entry = undo ? history.NextUndo : history.NextRedo;
            if (entry is null)
                throw new McpException(undo ? "There is nothing to undo." : "There is nothing to redo.");
            _ = undo ? ctx.Session.Undo() : ctx.Session.Redo();
            var project = ctx.Session.Project;
            return Task.FromResult(new EditResult(null, $"{(undo ? "Undid" : "Redid")} “{entry.Description}”.", Clips(project),
                Round(project.OutputDuration)));
        });

    /// <summary>Edit errors ("Clip 7 does not exist") go back to Claude as tool errors it can act on.</summary>
    private static T Guard<T>(Func<T> edit)
    {
        try
        {
            return edit();
        }
        catch (EditException e)
        {
            throw new McpException(e.Message);
        }
    }

    private static void RequireFile(IEditorContext ctx)
    {
        if (!ctx.HasFile)
            throw new McpException("No video is open in HighlightCut. Open one with open_file (list_videos finds recent ones).");
    }

    /// <summary>HighlightCut runs in its own folder, so a relative path would not mean what Claude meant.</summary>
    private static void RequireFullPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new McpException($"“{path}” is not a full path; give the whole path, e.g. from list_videos.");
    }

    /// <summary>
    /// Cuts <paramref name="cuts"/> (timeline ranges) out of the chosen clips (all included ones if null; every video
    /// whole, kept first, if there are no clips) as one undo step. <paramref name="describe"/> gets how many ranges touch
    /// those clips.
    /// </summary>
    private static EditResult CutOut(IEditorContext ctx, IReadOnlyList<TimeRange> cuts, IReadOnlyList<int>? clips, string name,
        Func<int, string> describe, string nothing)
    {
        var project = ctx.Session.Project;
        var steps = new List<IEditCommand>();
        if (clips is null && project.Clips.IsEmpty)
            steps.AddRange(project.Sources.Select(s => new AddClipCommand(0, s.Duration, project.Name, SourceId: s.Id)));
        IReadOnlyList<TimeRange> targets = clips is not null
            ? [.. clips.Select(id => project.Find(id) ?? throw new McpException($"Clip {id} does not exist.")).Select(project.TimelineRange)]
            : project.Clips.IsEmpty ? [new TimeRange(0, project.TimelineDuration)]
            : [.. project.IncludedClips.Select(project.TimelineRange)];
        var hit = cuts.Where(r => targets.Any(c => r.End > c.Start && r.Start < c.End)).ToList();
        if (hit.Count == 0)
            return new EditResult(null, nothing, Clips(project), Round(project.OutputDuration));
        string description = describe(hit.Count);
        steps.Add(new CutRangesCommand(TimelineEdits.ToSources(project, hit), clips, name, description));
        IEditCommand command = steps.Count == 1 ? steps[0] : new BatchCommand(name, description, steps);
        var entry = Guard(() => ctx.Session.Execute(command, EditOrigin.Assistant));
        // Without clips every video was kept first: the cut is measured against them.
        double before = project.Clips.IsEmpty && clips is null ? project.TimelineDuration : project.OutputDuration;
        return Result(ctx, entry) with
        {
            Result = $"{description}: {Round(before - ctx.Session.Project.OutputDuration)} s shorter.",
        };
    }

    /// <summary>The silences once all the audio is read (read now if the user's chips did not ask for it).</summary>
    private static async Task<SilenceReport> SilencesAsync(IEditorContext ctx, double minDuration, double? thresholdDb,
        IReadOnlyList<int>? tracks, CancellationToken cancellationToken)
    {
        Silences(ctx, minDuration, thresholdDb, tracks);
        await ctx.ReadAudioAsync(cancellationToken).ConfigureAwait(true);
        return Silences(ctx, minDuration, thresholdDb, tracks);
    }

    private static SilenceReport Silences(IEditorContext ctx, double minDuration, double? thresholdDb, IReadOnlyList<int>? tracks)
    {
        RequireFile(ctx);
        if (minDuration is < 0.05 or > 3600)
            throw new McpException("minDuration must be between 0.05 and 3600 seconds.");
        if (thresholdDb is < -90 or > 0)
            throw new McpException("thresholdDb must be between -90 and 0 dBFS.");
        // Track N is each video's Nth track; a video without it is judged by the tracks it has.
        int trackCount = ctx.Session.Project.Sources.Select(s => s.AudioTracks.Length).DefaultIfEmpty(0).Max();
        if (tracks is not null && tracks.Any(t => t < 1 || t > trackCount))
            throw new McpException($"There is no audio track {tracks.First(t => t < 1 || t > trackCount)}; the tracks are 1–{trackCount}.");
        return ctx.FindSilences(minDuration, thresholdDb, tracks?.Select(t => t - 1).ToList()) ?? throw new McpException("This file has no audio.");
    }

    private static string RangeText(double start, double end) => $"{TimeFormat.MinutesSeconds(start)}–{TimeFormat.MinutesSeconds(end)}";

    private static IEditCommand ToCommand(EditOperation op)
    {
        int Clip() => op.Clip ?? throw new EditException($"“{op.Action}” needs a clip id.");
        double Need(double? value, string name) => value ?? throw new EditException($"“{op.Action}” needs {name}.");
        return op.Action.Trim().ToLowerInvariant() switch
        {
            "add" => new AddRangeCommand(Need(op.Start, "start"), Need(op.End, "end"), op.Label, op.Position - 1,
                op.Color is null ? null : ParseColor(op.Color)),
            "remove" => new RemoveClipCommand(Clip()),
            "trim" => new PartialTrimCommand(Clip(), op.Start, op.End),
            "split" => new SplitAtCommand(Clip(), Need(op.Time, "time")),
            "join" => new JoinNextCommand(Clip()),
            "include" => new SetClipIncludedCommand(Clip(), true),
            "exclude" => new SetClipIncludedCommand(Clip(), false),
            "move" => new MoveClipCommand(Clip(), (op.Position ?? throw new EditException("“move” needs a position.")) - 1),
            "rename" => new RenameClipCommand(Clip(), op.Label ?? throw new EditException("“rename” needs a label.")),
            "color" or "colour" => new SetClipColorCommand(Clip(), ParseColor(op.Color ?? throw new EditException("“color” needs a color."))),
            _ => throw new EditException($"Unknown action “{op.Action}”; use add, remove, trim, split, join, include, exclude, move, rename or color."),
        };
    }

    private static ClipColor ParseColor(string name) =>
        ClipPalette.TryParse(name, out var color)
            ? color
            : throw new EditException($"“{name}” is not a clip colour; use {string.Join(", ", ClipPalette.Colors.Select(ClipPalette.Key))}.");

    private static EditResult Result(IEditorContext ctx, HistoryEntry? entry)
    {
        var project = ctx.Session.Project;
        return new EditResult(entry?.Id, entry?.Description ?? "Nothing changed.", Clips(project), Round(project.OutputDuration));
    }

    internal static ProjectInfo Describe(IEditorContext ctx)
    {
        var project = ctx.Session.Project;
        // The summary (codecs, resolution) is the open file's: the first video's.
        List<SourceInfo> sources = !ctx.HasFile ? [] : [.. project.Sources.Select((s, i) => new SourceInfo(s.Id, s.Path, s.FileName,
            (i == 0 ? ctx.SourceSummary : null) ?? s.FileName, Round(s.Duration), Round(project.OffsetOf(s.Id)), Math.Round(s.FrameRate, 3),
            [.. s.AudioTracks.Select((t, k) => TrackInfo(k + 1, t, project.MixOf(s.Id, t.Index)))]))];
        var source = sources.FirstOrDefault();
        return new ProjectInfo(project.Name, source, ctx.ProjectPath, Round(ctx.Playhead), ctx.SelectedClipId, ctx.IsPlaying,
            Round(project.OutputDuration), source is null ? [] : Clips(project), ctx.AnalysisStatus,
            source is null ? null : StatusText(ctx.TranscriptStatus), sources, source is null ? null : Round(project.TimelineDuration));
    }

    private static AudioTrackInfo TrackInfo(int number, AudioTrack track, TrackMix mix) =>
        new(number, track.Label, mix.GainDb, mix.IsMuted);

    /// <summary>The clips in output order, with their timeline ranges.</summary>
    private static List<ClipInfo> Clips(Project project) =>
        [.. project.Clips.Select((c, i) => (Clip: c, Range: project.TimelineRange(c), Index: i)).Select(x => new ClipInfo(x.Clip.Id, x.Index + 1,
            x.Clip.Label, Round(x.Range.Start), Round(x.Range.End), Round(x.Clip.Duration), x.Clip.IsIncluded, RangeText(x.Range.Start, x.Range.End),
            ClipPalette.Key(x.Clip.Color), x.Clip.SourceId))];

    private static double Round(double seconds) => Math.Round(seconds, 3);

    private static string DefaultVideosFolder()
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return videos.Length > 0 ? videos : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
    }
}

/// <summary>
/// Moves one or both ends of a clip to timeline times; the other stays where it is when the edit is applied. The times
/// must be in the clip's video.
/// </summary>
internal sealed record PartialTrimCommand(int ClipId, double? Start, double? End) : IEditCommand
{
    public string Name => "trim_segment";

    public string Describe(Project before) => Resolve(before).Describe(before);

    public Project Apply(Project project) => Resolve(project).Apply(project);

    private SetClipRangeCommand Resolve(Project project)
    {
        if (Start is null && End is null)
            throw new EditException("Give a new start, a new end or both.");
        var clip = project.Get(ClipId);
        return new SetClipRangeCommand(ClipId, Start is { } start ? TimelineEdits.OnClipVideo(project, clip, start) : clip.Start,
            End is { } end ? TimelineEdits.OnClipVideo(project, clip, end) : clip.End);
    }
}

/// <summary>Splits a clip at a timeline time, mapped onto its video when the edit is applied.</summary>
internal sealed record SplitAtCommand(int ClipId, double Time) : IEditCommand
{
    public string Name => "split_segment";

    public string Describe(Project before) => Resolve(before).Describe(before);

    public Project Apply(Project project) => Resolve(project).Apply(project);

    private SplitClipCommand Resolve(Project project) =>
        new(ClipId, TimelineEdits.OnClipVideo(project, project.Get(ClipId), Time));
}

/// <summary>
/// Keeps a timeline range: one clip, or one per video when it crosses a join (see <see cref="TimelineEdits.AddRange"/>),
/// mapped onto the videos when the edit is applied.
/// </summary>
internal sealed record AddRangeCommand(double Start, double End, string? Label, int? Index, ClipColor? Color) : IEditCommand
{
    public string Name => "add_segment";

    public string Describe(Project before) => Resolve(before).Describe(before);

    public Project Apply(Project project) => Resolve(project).Apply(project);

    private IEditCommand Resolve(Project project) => TimelineEdits.AddRange(project, Start, End, Label, Index, Color);
}

/// <summary>Joins a clip with the one after it, found when the edit is applied (after the operations before it).</summary>
internal sealed record JoinNextCommand(int ClipId) : IEditCommand
{
    public string Name => "join_segments";

    public string Describe(Project before) => JoinClipsCommand.WithNext(before, ClipId).Describe(before);

    public Project Apply(Project project) => JoinClipsCommand.WithNext(project, ClipId).Apply(project);
}
