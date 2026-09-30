using HighlightCut.Core.Editing.Commands;
using HighlightCut.Core.Model;
using HighlightCut.Core.Timeline;

namespace HighlightCut.Core.Editing;

public enum ProjectChangeKind
{
    Loaded,
    Edited,
    Undone,
    Redone,

    /// <summary>A track's volume or mute changed (<see cref="EditorSession.SetTrackMix"/>); not in the history.</summary>
    Mixed,
}

public sealed class ProjectChangedEventArgs(ProjectChangeKind kind, Project previous, Project current, HistoryEntry? entry)
    : EventArgs
{
    public ProjectChangeKind Kind { get; } = kind;
    public Project Previous { get; } = previous;
    public Project Current { get; } = current;

    /// <summary>The edit that was applied, undone or redone; null when a project is loaded.</summary>
    public HistoryEntry? Entry { get; } = entry;
}

/// <summary>
/// The editing API for one open project. The UI and the MCP server call the same methods;
/// each edit becomes a command in <see cref="History"/>. The helpers take timeline seconds (the videos end to end, what
/// the user sees) and map them onto the videos; the commands themselves take seconds on one video.
/// </summary>
/// <remarks>Not thread-safe: call it from one thread (the UI thread) and marshal other callers to it.</remarks>
public sealed class EditorSession
{
    private IReadOnlyList<double> _keyframes = [];

    public EditorSession(Project? project = null, int historyCapacity = 1000)
    {
        Project = project ?? Project.Empty;
        History = new History(historyCapacity);
    }

    public Project Project { get; private set; }

    public History History { get; }

    /// <summary>
    /// Sorted keyframe times on the timeline, used for snapping: each video's keyframes moved by its offset. Whoever sets
    /// them sets them again when the videos change.
    /// </summary>
    public IReadOnlyList<double> Keyframes
    {
        get => _keyframes;
        set => _keyframes = [.. value.Order()];
    }

    public event EventHandler<ProjectChangedEventArgs>? Changed;

    /// <summary>Replaces the project and clears the history.</summary>
    public void Load(Project project)
    {
        var previous = Project;
        Project = project;
        History.Clear();
        Changed?.Invoke(this, new ProjectChangedEventArgs(ProjectChangeKind.Loaded, previous, project, null));
    }

    /// <summary>
    /// Applies a command and records it. Edits with the same <paramref name="mergeKey"/> in a row
    /// (e.g. the steps of one drag) undo as one step. Returns null if the command changed nothing.
    /// </summary>
    /// <exception cref="EditException">The command is not valid for the current project.</exception>
    public HistoryEntry? Execute(IEditCommand command, EditOrigin origin = EditOrigin.User, string? mergeKey = null)
    {
        var before = Project;
        var after = command.Apply(before);
        if (ReferenceEquals(after, before))
            return null;
        EditRules.ValidateNoNewOverlap(before, after);
        EditRules.ValidateClipSources(after);
        var entry = History.Push(new HistoryEntry(command, command.Describe(before), before, after, origin,
            DateTimeOffset.Now, mergeKey));
        Project = after;
        Changed?.Invoke(this, new ProjectChangedEventArgs(ProjectChangeKind.Edited, before, after, entry));
        return entry;
    }

    public bool Undo()
    {
        var entry = History.StepBack();
        if (entry is null)
            return false;
        var previous = Project;
        Project = KeepMix(entry.Before, previous);
        Changed?.Invoke(this, new ProjectChangedEventArgs(ProjectChangeKind.Undone, previous, Project, entry));
        return true;
    }

    public bool Redo()
    {
        var entry = History.StepForward();
        if (entry is null)
            return false;
        var previous = Project;
        Project = KeepMix(entry.After, previous);
        Changed?.Invoke(this, new ProjectChangedEventArgs(ProjectChangeKind.Redone, previous, Project, entry));
        return true;
    }

    /// <summary>
    /// Sets one audio track's volume and mute. Like the mute toggle this is a mixer setting, not an edit: it is saved
    /// with the project but not recorded in <see cref="History"/>, and undo and redo leave it as it is.
    /// </summary>
    /// <returns>False if nothing changed.</returns>
    public bool SetTrackMix(TrackMix mix)
    {
        var before = Project;
        var after = before.WithMix(mix with { GainDb = TrackMix.ClampGain(mix.GainDb) });
        if (ReferenceEquals(after, before))
            return false;
        Project = after;
        Changed?.Invoke(this, new ProjectChangedEventArgs(ProjectChangeKind.Mixed, before, after, null));
        return true;
    }

    /// <summary>A project from the history with the mix the user has now.</summary>
    private static Project KeepMix(Project fromHistory, Project current) =>
        ReferenceEquals(fromHistory.AudioMix, current.AudioMix) ? fromHistory : fromHistory with { AudioMix = current.AudioMix };

    /// <summary>Undoes <paramref name="entry"/> and everything after it.</summary>
    public void UndoThrough(HistoryEntry entry)
    {
        while (History.IsApplied(entry) && Undo())
        {
        }
    }

    /// <summary>Redoes everything up to and including <paramref name="entry"/>.</summary>
    public void RedoThrough(HistoryEntry entry)
    {
        while (!History.IsApplied(entry) && History.CanRedo && Redo())
        {
        }
    }

    /// <summary>
    /// Reverts one earlier edit as a new edit, keeping everything done after it
    /// (see <see cref="RevertEditCommand"/>).
    /// </summary>
    /// <exception cref="EditException">A later edit changed the same clips, or the edit is not applied.</exception>
    public HistoryEntry? Revert(HistoryEntry entry, EditOrigin origin = EditOrigin.User)
    {
        if (!History.IsApplied(entry))
            throw new EditException($"“{entry.Description}” is not applied.");
        if (IsReverted(entry))
            throw new EditException($"“{entry.Description}” was already reverted.");
        return Execute(RevertEditCommand.For(entry), origin);
    }

    /// <summary>
    /// Whether an applied edit reverts <paramref name="entry"/> (and that revert has not been reverted in turn).
    /// </summary>
    public bool IsReverted(HistoryEntry entry) => RevertOf(entry) is not null;

    /// <summary>The applied edit that reverts <paramref name="entry"/>, if any.</summary>
    public HistoryEntry? RevertOf(HistoryEntry entry)
    {
        for (int i = History.Position - 1; i >= 0; i--)
        {
            var e = History.Entries[i];
            if (ReferenceEquals(e, entry))
                break;
            if (e.Command is RevertEditCommand r && r.Reverts(entry) && !IsReverted(e))
                return e;
        }
        return null;
    }

    // ---- Operations --------------------------------------------------------------------------

    /// <summary>
    /// Adds a clip for a timeline range at the end of the output (or at <paramref name="index"/>). A range over a join
    /// becomes one clip per video, in one undo step (see <see cref="TimelineEdits.AddRange"/>). Returns the first.
    /// </summary>
    public Clip AddClip(double start, double end, string? label = null, int? index = null, EditOrigin origin = EditOrigin.User)
    {
        int id = Project.NextClipId;
        Execute(TimelineEdits.AddRange(Project, start, end, label, index), origin);
        return Project.Get(id);
    }

    /// <summary>
    /// Adds a clip for a timeline range, placed among the clips by timeline position ("+ Keep", "Keep as clip"). Clips do
    /// not overlap, so the range is cut down to its free part (see <see cref="Project.FreeTimelineRange"/>); a free part
    /// over a join becomes one clip per video, in one undo step. Returns the first new clip.
    /// </summary>
    /// <exception cref="EditException">Other clips already cover the range, or all but a sliver of it.</exception>
    public Clip KeepRange(double start, double end, string? label = null, EditOrigin origin = EditOrigin.User)
    {
        var project = Project;
        List<SourceRange> parts = project.FreeTimelineRange(start, end) is { } free
            ? [.. project.SplitAtSources(free.Start, free.End).Where(p => p.Duration >= EditRules.MinClipDuration - EditRules.Epsilon)]
            : [];
        if (parts.Count == 0)
        {
            throw new EditException(project.FirstOverlappingOnTimeline(start, end) is { } clip
                ? $"That is already in clip {project.NumberOf(clip.Id)}."
                : $"Clips must be at least {EditRules.MinClipDuration} s long.");
        }
        // Each part goes before the first clip (in output order) that starts after it on the timeline.
        var adds = new List<IEditCommand>();
        var next = project;
        foreach (var part in parts)
        {
            double at = next.ToTimeline(part.SourceId, part.Start);
            int index = next.Clips.FindIndex(c => next.TimelineRange(c).Start > at);
            var add = new AddClipCommand(part.Start, part.End, label, index < 0 ? next.Clips.Count : index, next.NextClipId,
                SourceId: part.SourceId);
            next = add.Apply(next);
            adds.Add(add);
        }
        int first = project.NextClipId;
        Execute(adds.Count == 1 ? adds[0] : new BatchCommand("add_segment", $"Added {adds.Count} clips, one per video", adds), origin);
        return Project.Get(first);
    }

    /// <summary>Sets a clip's in- and out-point, given as timeline times; they must be in the clip's video.</summary>
    public void SetRange(int clipId, double start, double end, EditOrigin origin = EditOrigin.User, string? mergeKey = null)
    {
        var clip = Project.Get(clipId);
        Execute(new SetClipRangeCommand(clipId, TimelineEdits.OnClipVideo(Project, clip, start), TimelineEdits.OnClipVideo(Project, clip, end)),
            origin, mergeKey);
    }

    /// <summary>
    /// Moves one end of a clip to a timeline time, clamped so the clip stays inside its video, at least
    /// <see cref="EditRules.MinClipDuration"/> long and clear of its neighbours in that video (it stops at their edge).
    /// Within <paramref name="snapThreshold"/> seconds (0 turns snapping off) the end snaps onto the neighbour's edge, so
    /// the two clips touch, or else, with <paramref name="snapToKeyframes"/>, onto the nearest keyframe. Returns the new
    /// time of that end on the timeline.
    /// </summary>
    public double Trim(int clipId, ClipEdge edge, double time, double snapThreshold = 0, string? mergeKey = null,
        EditOrigin origin = EditOrigin.User, bool snapToKeyframes = true)
    {
        var clip = Project.Get(clipId);
        double offset = Project.FindSource(clip.SourceId) is null ? 0 : Project.OffsetOf(clip.SourceId);
        // On the clip's video: the neighbour's edge and the video's ends are exact there.
        double limit = Project.TrimLimit(clip, edge);
        double t = snapThreshold > 0 && Math.Abs(time - offset - limit) <= snapThreshold ? limit
            : Snapping.ToNearest(snapToKeyframes ? Keyframes : [], time, snapThreshold) - offset;
        if (edge == ClipEdge.In)
        {
            t = Math.Clamp(t, limit, Math.Max(limit, clip.End - EditRules.MinClipDuration));
            Execute(new SetClipRangeCommand(clipId, t, clip.End), origin, mergeKey);
        }
        else
        {
            t = Math.Clamp(t, Math.Min(limit, clip.Start + EditRules.MinClipDuration), limit);
            Execute(new SetClipRangeCommand(clipId, clip.Start, t), origin, mergeKey);
        }
        return offset + t;
    }

    /// <summary>
    /// Joins a clip with the one right after it on its video (see <see cref="JoinClipsCommand"/>).
    /// Returns the joined clip.
    /// </summary>
    /// <exception cref="EditException">There is no clip after it in its video, or the two cannot be joined.</exception>
    public Clip JoinWithNext(int clipId, EditOrigin origin = EditOrigin.User)
    {
        Execute(JoinClipsCommand.WithNext(Project, clipId), origin);
        return Project.Get(clipId);
    }

    /// <summary>Whether <see cref="JoinWithNext"/> would work for the clip.</summary>
    public bool CanJoinWithNext(int clipId)
    {
        try
        {
            return JoinClipsCommand.WithNext(Project, clipId).Problem(Project) is null;
        }
        catch (EditException)
        {
            return false;
        }
    }

    /// <summary>Splits a clip at a timeline time (in the clip's video) and returns the second part.</summary>
    public Clip Split(int clipId, double time, EditOrigin origin = EditOrigin.User)
    {
        var clip = Project.Get(clipId);
        int id = Project.NextClipId;
        Execute(new SplitClipCommand(clipId, TimelineEdits.OnClipVideo(Project, clip, time)), origin);
        return Project.Get(id);
    }

    public void SetIncluded(int clipId, bool included, EditOrigin origin = EditOrigin.User) =>
        Execute(new SetClipIncludedCommand(clipId, included), origin);

    public void Move(int clipId, int toIndex, EditOrigin origin = EditOrigin.User) =>
        Execute(new MoveClipCommand(clipId, toIndex), origin);

    public void Rename(int clipId, string label, EditOrigin origin = EditOrigin.User) =>
        Execute(new RenameClipCommand(clipId, label), origin);

    public void SetColor(int clipId, ClipColor color, EditOrigin origin = EditOrigin.User) =>
        Execute(new SetClipColorCommand(clipId, color), origin);

    public void Remove(int clipId, EditOrigin origin = EditOrigin.User) =>
        Execute(new RemoveClipCommand(clipId), origin);

    // ---- Videos ------------------------------------------------------------------------------

    /// <summary>Adds a video at the end of the timeline and returns it with the id it got.</summary>
    public SourceMedia AddSource(SourceMedia source, EditOrigin origin = EditOrigin.User)
    {
        int id = Project.NextSourceId;
        Execute(new AddSourceCommand(source), origin);
        return Project.GetSource(id);
    }

    /// <summary>Removes a video and every clip cut from it, as one undo step.</summary>
    public void RemoveSource(int sourceId, EditOrigin origin = EditOrigin.User) =>
        Execute(new RemoveSourceCommand(sourceId), origin);

    /// <summary>Moves a video to another place on the timeline (0-based); its clips move with it.</summary>
    public void MoveSource(int sourceId, int toIndex, EditOrigin origin = EditOrigin.User) =>
        Execute(new MoveSourceCommand(sourceId, toIndex), origin);
}
