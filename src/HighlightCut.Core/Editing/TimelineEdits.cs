using HighlightCut.Core.Editing.Commands;
using HighlightCut.Core.Model;
using HighlightCut.Core.Time;

namespace HighlightCut.Core.Editing;

/// <summary>
/// Edits given in timeline seconds, the times the user sees and Claude uses, mapped onto the videos. Commands work in
/// seconds on one video; these turn a timeline range into one clip per video it covers, and a timeline time into a time
/// on a clip's video. With one video the timeline is that video, so nothing changes.
/// </summary>
public static class TimelineEdits
{
    /// <summary>
    /// The parts of a timeline range that become clips: one per video it covers. A sliver shorter than
    /// <see cref="EditRules.MinClipDuration"/> on one side of a join is left out.
    /// </summary>
    /// <exception cref="EditException">The range is not on the timeline, or too short for a clip in every video.</exception>
    public static IReadOnlyList<SourceRange> ClipParts(Project project, double start, double end)
    {
        // One video (or none): the timeline is that video, and the command checks the range as it always has.
        if (project.Sources.Count <= 1)
            return [new SourceRange(project.Source?.Id ?? SourceMedia.FirstId, start, end)];
        if (!double.IsFinite(start) || !double.IsFinite(end))
            throw new EditException("Clip times must be finite numbers.");
        double total = project.TimelineDuration;
        if (start < -EditRules.Epsilon)
            throw new EditException($"In-point {start:0.###} s is before the start of the timeline.");
        if (end > total + EditRules.Epsilon)
            throw new EditException($"Out-point {end:0.###} s is after the end of the timeline ({total:0.###} s).");
        if (end - start < EditRules.MinClipDuration - EditRules.Epsilon)
            throw new EditException($"Clips must be at least {EditRules.MinClipDuration} s long.");
        var parts = project.SplitAtSources(start, end).Where(p => p.Duration >= EditRules.MinClipDuration - EditRules.Epsilon).ToList();
        return parts.Count > 0
            ? parts
            : throw new EditException($"That range has less than {EditRules.MinClipDuration} s in each video; a clip cannot cross " +
                                      "from one video into the next.");
    }

    /// <summary>
    /// Adds clips for a timeline range: one, or one per video when the range crosses a join, at <paramref name="index"/>
    /// and the places after it (the end of the output if null). One command, so one undo step. Names and colours are
    /// given as <see cref="AddClipCommand"/> gives them: a second part of a named range gets " · 2".
    /// </summary>
    public static IEditCommand AddRange(Project project, double start, double end, string? label = null, int? index = null,
        ClipColor? color = null)
    {
        var parts = ClipParts(project, start, end);
        var adds = parts.Select((p, k) => new AddClipCommand(p.Start, p.End, label, index + k, Color: color, SourceId: p.SourceId)).ToList();
        return adds.Count == 1 ? adds[0] : new BatchCommand("add_segment", $"Added {adds.Count} clips, one per video", adds);
    }

    /// <summary>
    /// A timeline time as seconds on a clip's video, to trim or split that clip.
    /// </summary>
    /// <exception cref="EditException">The time is in another video: a clip never crosses into the next one.</exception>
    public static double OnClipVideo(Project project, Clip clip, double time)
    {
        if (project.Sources.Count <= 1)
            return time;
        var span = project.SpanOf(clip.SourceId);
        if (!(time >= span.Start - EditRules.Epsilon && time <= span.End + EditRules.Epsilon))
        {
            throw new EditException($"Clip {project.NumberOf(clip.Id)} is in video {project.SourceNumberOf(clip.SourceId)}, which runs " +
                                    $"{TimeFormat.MinutesSeconds(span.Start)} – {TimeFormat.MinutesSeconds(span.End)} on the timeline; " +
                                    $"{TimeFormat.MinutesSeconds(time)} is outside it, and a clip cannot cross into another video.");
        }
        return Math.Clamp(time - span.Start, 0, span.Duration);
    }

    /// <summary>Timeline ranges as ranges of the videos they cover, cut at the joins (e.g. to cut them out of clips).</summary>
    public static IReadOnlyList<SourceRange> ToSources(Project project, IEnumerable<TimeRange> ranges) =>
        [.. ranges.SelectMany(r => project.SplitAtSources(r.Start, r.End))];
}
