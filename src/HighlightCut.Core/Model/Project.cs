using System.Collections.Immutable;

using HighlightCut.Core.Editing;

namespace HighlightCut.Core.Model;

/// <summary>A range of seconds, e.g. an excluded gap between clips.</summary>
public readonly record struct TimeRange(double Start, double End)
{
    public double Duration => End - Start;
}

/// <summary>A time on one of the project's videos, in seconds on that video.</summary>
public readonly record struct SourceTime(int SourceId, double Time);

/// <summary>A range of one of the project's videos, in seconds on that video.</summary>
public readonly record struct SourceRange(int SourceId, double Start, double End)
{
    public double Duration => End - Start;
}

/// <summary>
/// A stretch of the export: an included clip, or the part of it that earlier clips have not already exported. Times are
/// seconds on the clip's video.
/// </summary>
public readonly record struct OutputPart(Clip Clip, double Start, double End)
{
    public double Duration => End - Start;
}

/// <summary>
/// An editing project: one or more videos and the clips cut from them. The videos sit end to end on one timeline in the
/// order of <see cref="Sources"/>; the order of <see cref="Clips"/> is the output order. A clip belongs to one video and
/// its times are seconds on that video (see <see cref="TimelineRange"/> for its place on the timeline). Projects are
/// immutable; edit commands return new instances.
/// </summary>
public sealed record Project(string Name, ImmutableList<SourceMedia> Sources, ImmutableList<Clip> Clips)
{
    /// <summary>A project with one video (or none), like every project before projects could have several.</summary>
    public Project(string name, SourceMedia? source, ImmutableList<Clip> clips)
        : this(name, source is null ? ImmutableList<SourceMedia>.Empty : [source], clips)
    {
    }

    /// <summary>Gaps shorter than this are not reported as excluded ranges.</summary>
    public const double MinimumGap = 0.3;

    public static Project Empty { get; } = new("Untitled project", ImmutableList<SourceMedia>.Empty, []);

    // ---- Videos ------------------------------------------------------------------------------

    /// <summary>The first video: the only one in a project with one video. Null in a project without one.</summary>
    public SourceMedia? Source => Sources.IsEmpty ? null : Sources[0];

    public SourceMedia? FindSource(int id) => Sources.Find(s => s.Id == id);

    public SourceMedia GetSource(int id) => FindSource(id) ?? throw new EditException($"Video {id} is not in the project.");

    public int SourceIndexOf(int id) => Sources.FindIndex(s => s.Id == id);

    /// <summary>1-based position of a video on the timeline, as messages name it ("video 2").</summary>
    public int SourceNumberOf(int id) => SourceIndexOf(id) + 1;

    /// <summary>The clips cut from a video, in output order.</summary>
    public IEnumerable<Clip> ClipsOf(int sourceId) => Clips.Where(c => c.SourceId == sourceId);

    /// <summary>
    /// The highest video id handed out in this project, including videos removed since. Like <see cref="LastClipId"/>
    /// it only goes up, so a removed video's id is never given to another one. Saved with the project.
    /// </summary>
    public int LastSourceId { get; init; }

    public int NextSourceId => Math.Max(LastSourceId, Sources.IsEmpty ? 0 : Sources.Max(s => s.Id)) + 1;

    /// <summary>The project with <paramref name="sources"/>, and <see cref="LastSourceId"/> raised to cover their ids.</summary>
    public Project WithSources(ImmutableList<SourceMedia> sources) =>
        this with { Sources = sources, LastSourceId = Math.Max(NextSourceId - 1, sources.IsEmpty ? 0 : sources.Max(s => s.Id)) };

    /// <summary>The project with <paramref name="source"/> in place of the video with its id, e.g. the file probed again.</summary>
    public Project WithSource(SourceMedia source)
    {
        int i = SourceIndexOf(source.Id);
        if (i < 0)
            throw new EditException($"Video {source.Id} is not in the project.");
        return this with { Sources = Sources.SetItem(i, source) };
    }

    // ---- The timeline ------------------------------------------------------------------------
    // The videos end to end. A project without videos (the empty project, tests) has an open-ended timeline at 0 on
    // which every clip sits as it is.

    /// <summary>Length of the timeline: every video, end to end.</summary>
    public double TimelineDuration => Sources.Sum(s => s.Duration);

    /// <summary>Where a video starts on the timeline: the length of the videos before it.</summary>
    /// <exception cref="EditException">The project does not have that video.</exception>
    public double OffsetOf(int sourceId)
    {
        double offset = 0;
        foreach (var s in Sources)
        {
            if (s.Id == sourceId)
                return offset;
            offset += s.Duration;
        }
        return Sources.IsEmpty ? 0 : throw new EditException($"Video {sourceId} is not in the project.");
    }

    /// <summary>The part of the timeline a video takes.</summary>
    public TimeRange SpanOf(int sourceId)
    {
        double offset = OffsetOf(sourceId);
        return new TimeRange(offset, offset + (FindSource(sourceId)?.Duration ?? 0));
    }

    /// <summary>A time on a video as a time on the timeline.</summary>
    public double ToTimeline(int sourceId, double time) => OffsetOf(sourceId) + time;

    public double ToTimeline(SourceTime time) => ToTimeline(time.SourceId, time.Time);

    /// <summary>Where a clip is on the timeline.</summary>
    public TimeRange TimelineRange(Clip clip)
    {
        double offset = OffsetOf(clip.SourceId);
        return new TimeRange(offset + clip.Start, offset + clip.End);
    }

    /// <summary>
    /// The video at a time on the timeline and the time on that video. A join belongs to the video that starts there;
    /// the very end of the timeline to the last video. Null outside the timeline and in a project without videos.
    /// </summary>
    public SourceTime? ToSource(double time)
    {
        if (!double.IsFinite(time) || time < -EditRules.Epsilon)
            return null;
        double offset = 0;
        for (int i = 0; i < Sources.Count; i++)
        {
            var s = Sources[i];
            double end = offset + s.Duration;
            if (time < end || (i == Sources.Count - 1 && time <= end + EditRules.Epsilon))
                return new SourceTime(s.Id, Math.Clamp(time - offset, 0, s.Duration));
            offset = end;
        }
        return null;
    }

    /// <summary>The video that covers a time on the timeline (see <see cref="ToSource"/>).</summary>
    public SourceMedia? SourceAt(double time) => ToSource(time) is { } t ? FindSource(t.SourceId) : null;

    /// <summary>
    /// A timeline range cut at the joins between videos: its part on each video it covers, in timeline order and in
    /// seconds on that video. Parts of no length (a range that ends exactly at a join) and whatever lies outside the
    /// timeline are left out.
    /// </summary>
    public IReadOnlyList<SourceRange> SplitAtSources(double start, double end)
    {
        if (Sources.IsEmpty)
            return end - start > EditRules.Epsilon ? [new SourceRange(SourceMedia.FirstId, start, end)] : [];
        var parts = new List<SourceRange>();
        double offset = 0;
        foreach (var s in Sources)
        {
            double from = Math.Clamp(start - offset, 0, s.Duration);
            double to = Math.Clamp(end - offset, 0, s.Duration);
            if (to - from > EditRules.Epsilon)
                parts.Add(new SourceRange(s.Id, from, to));
            offset += s.Duration;
        }
        return parts;
    }

    // ---- Audio -------------------------------------------------------------------------------

    /// <summary>
    /// Volume and mute of the audio tracks that differ from the default (0 dB, unmuted), by video and stream index. The
    /// mix is not part of the undo history (see <see cref="EditorSession.SetTrackMix"/>). A removed video's entries stay,
    /// so undoing the removal brings the video back with its mix; the file keeps only the videos' own tracks.
    /// </summary>
    public ImmutableList<TrackMix> AudioMix { get; init; } = [];

    /// <summary>The mix of the audio stream with container index <paramref name="index"/> of a video.</summary>
    public TrackMix MixOf(int sourceId, int index) =>
        AudioMix.Find(m => m.SourceId == sourceId && m.Index == index) ?? new TrackMix(index, SourceId: sourceId);

    /// <summary>The project with <paramref name="mix"/> for its track; a default mix removes the entry.</summary>
    public Project WithMix(TrackMix mix)
    {
        var list = AudioMix.RemoveAll(m => m.SourceId == mix.SourceId && m.Index == mix.Index);
        if (!mix.IsDefault)
            list = list.Add(mix).Sort((a, b) => a.SourceId != b.SourceId ? a.SourceId.CompareTo(b.SourceId) : a.Index.CompareTo(b.Index));
        return list.SequenceEqual(AudioMix) ? this : this with { AudioMix = list };
    }

    // ---- Clips -------------------------------------------------------------------------------

    public IEnumerable<Clip> IncludedClips => Clips.Where(c => c.IsIncluded);

    /// <summary>Length of the export: the included clips back to back, each second of a video at most once.</summary>
    public double OutputDuration => OutputParts().Sum(p => p.Duration);

    /// <summary>Overlaps shorter than this (rounding in times from elsewhere) do not count.</summary>
    public const double OverlapTolerance = 1e-6;

    /// <summary>Pieces of output shorter than this are dropped (a thousandth of a second is less than a frame).</summary>
    private const double MinimumPart = 1e-3;

    /// <summary>
    /// What the export plays, in output order. Clips do not overlap, so normally this is every included clip whole. A
    /// project saved before that rule may have clips sharing time on their video: a clip then loses the seconds an
    /// earlier clip in the output already has (and is left out, or split in two, if that covers it), so nothing is
    /// exported twice. Clips of different videos never share time.
    /// </summary>
    public IReadOnlyList<OutputPart> OutputParts()
    {
        var parts = new List<OutputPart>();
        var taken = new Dictionary<int, List<TimeRange>>();
        foreach (var clip in IncludedClips)
        {
            if (!taken.TryGetValue(clip.SourceId, out var onVideo))
                taken[clip.SourceId] = onVideo = [];
            double from = clip.Start;
            foreach (var r in onVideo.Where(r => r.End > clip.Start && r.Start < clip.End).OrderBy(r => r.Start))
            {
                if (r.Start - from >= MinimumPart)
                    parts.Add(new OutputPart(clip, from, r.Start));
                from = Math.Max(from, r.End);
            }
            if (clip.End - from >= MinimumPart)
                parts.Add(new OutputPart(clip, from, clip.End));
            onVideo.Add(new TimeRange(clip.Start, clip.End));
        }
        return parts;
    }

    /// <summary>
    /// Pairs of clips that share time on their video, the earlier one first. Edits never create them; projects saved
    /// before that rule can have them.
    /// </summary>
    public IReadOnlyList<(Clip First, Clip Second)> Overlaps()
    {
        var pairs = new List<(Clip, Clip)>();
        var sorted = Clips.OrderBy(c => c.SourceId).ThenBy(c => c.Start).ThenBy(c => c.End).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            for (int j = i + 1; j < sorted.Count && sorted[j].SourceId == sorted[i].SourceId
                                 && sorted[j].Start < sorted[i].End - OverlapTolerance; j++)
            {
                if (Math.Min(sorted[i].End, sorted[j].End) - sorted[j].Start > OverlapTolerance)
                    pairs.Add((sorted[i], sorted[j]));
            }
        }
        return pairs;
    }

    /// <summary>Seconds of video shared by two clips, summed over all such pairs.</summary>
    public double OverlapDuration() => Overlaps().Sum(p => Math.Min(p.First.End, p.Second.End) - p.Second.Start);

    /// <summary>The first clip of a video, other than <paramref name="exceptId"/>, that shares time with the range, if any.</summary>
    public Clip? FirstOverlapping(int sourceId, double start, double end, int? exceptId = null) =>
        ClipsOf(sourceId).Where(c => c.Id != exceptId && Math.Min(end, c.End) - Math.Max(start, c.Start) > OverlapTolerance)
            .OrderBy(c => c.Start).FirstOrDefault();

    /// <summary>The first clip on the timeline that shares time with a timeline range, if any.</summary>
    public Clip? FirstOverlappingOnTimeline(double start, double end) =>
        Clips.Select(c => (Clip: c, Range: TimelineRange(c)))
            .Where(x => Math.Min(end, x.Range.End) - Math.Max(start, x.Range.Start) > OverlapTolerance)
            .OrderBy(x => x.Range.Start).Select(x => x.Clip).FirstOrDefault();

    /// <summary>
    /// The part of <paramref name="start"/>–<paramref name="end"/> on a video that no clip covers: from
    /// <paramref name="start"/> (or, when a clip covers it, from where that clip ends) to the next clip. Null when less
    /// than <see cref="EditRules.MinClipDuration"/> of it is free.
    /// </summary>
    public TimeRange? FreeRange(int sourceId, double start, double end) =>
        Free(ClipsOf(sourceId).Select(c => new TimeRange(c.Start, c.End)), start, end) is { } free
            && free.Duration >= EditRules.MinClipDuration - EditRules.Epsilon ? free : null;

    /// <summary>
    /// <see cref="FreeRange"/> on the timeline, across the joins between videos: from <paramref name="start"/> (or where
    /// a clip covering it ends) to the next clip on the timeline, whichever video it is in. Null when nothing is free.
    /// </summary>
    public TimeRange? FreeTimelineRange(double start, double end) =>
        Free(Clips.Select(TimelineRange), start, end) is { } free && free.Duration > EditRules.Epsilon ? free : null;

    private static TimeRange Free(IEnumerable<TimeRange> taken, double start, double end)
    {
        var sorted = taken.OrderBy(r => r.Start).ToList();
        double from = start;
        foreach (var r in sorted)
        {
            if (r.Start <= from + OverlapTolerance && r.End > from)
                from = r.End;
        }
        double to = Math.Min(end, sorted.Where(r => r.Start >= from - OverlapTolerance && r.End > from).Select(r => r.Start).DefaultIfEmpty(end).Min());
        return new TimeRange(from, to);
    }

    /// <summary>
    /// How far one end of a clip may move before it runs into another clip of its video: the out-point of the clip
    /// before it (<see cref="ClipEdge.In"/>) or the in-point of the clip after it (<see cref="ClipEdge.Out"/>), or the
    /// start or end of the video. Never past where that end already is, so a clip that overlaps another (an old project)
    /// can shrink but not grow into it. Seconds on the clip's video.
    /// </summary>
    public double TrimLimit(Clip clip, ClipEdge edge)
    {
        var others = ClipsOf(clip.SourceId).Where(c => c.Id != clip.Id);
        if (edge == ClipEdge.In)
            return Math.Min(clip.Start, others.Where(c => c.Start < clip.Start).Select(c => c.End).DefaultIfEmpty(0).Max());
        double end = FindSource(clip.SourceId)?.Duration ?? double.MaxValue;
        return Math.Max(clip.End, others.Where(c => c.End > clip.End).Select(c => c.Start).DefaultIfEmpty(end).Min());
    }

    public Clip? Find(int id) => Clips.FirstOrDefault(c => c.Id == id);

    public Clip Get(int id) => Find(id) ?? throw new EditException($"Clip {id} does not exist.");

    public int IndexOf(int id) => Clips.FindIndex(c => c.Id == id);

    /// <summary>1-based position of a clip in the output order, as shown in the UI.</summary>
    public int NumberOf(int id) => IndexOf(id) + 1;

    /// <summary>
    /// The highest clip id handed out in this project, including clips deleted since. It only goes up, so a deleted
    /// clip's id (and its "Clip N" name) is never given to a new clip. Saved with the project.
    /// </summary>
    public int LastClipId { get; init; }

    public int NextClipId => Math.Max(LastClipId, Clips.IsEmpty ? 0 : Clips.Max(c => c.Id)) + 1;

    /// <summary>
    /// The project with <paramref name="clips"/>, and <see cref="LastClipId"/> raised to cover the ids of the clips it
    /// had and has, so removing the clip with the highest id does not free that id.
    /// </summary>
    public Project WithClips(ImmutableList<Clip> clips) =>
        this with { Clips = clips, LastClipId = Math.Max(NextClipId - 1, clips.IsEmpty ? 0 : clips.Max(c => c.Id)) };

    /// <summary>First clip in output order that contains <paramref name="time"/>, a time on the timeline.</summary>
    public Clip? ClipAt(double time) => Clips.FirstOrDefault(c => TimelineRange(c) is var r && time >= r.Start && time <= r.End);

    /// <summary>
    /// Timeline ranges not covered by any clip (included or not), in timeline order. A gap never runs across a join: each
    /// video's gaps are its own.
    /// </summary>
    public IReadOnlyList<TimeRange> UncoveredRanges()
    {
        var gaps = new List<TimeRange>();
        if (Sources.IsEmpty)
            AddGaps(gaps, Clips, 0, 0);
        double offset = 0;
        foreach (var s in Sources)
        {
            AddGaps(gaps, ClipsOf(s.Id), offset, s.Duration);
            offset += s.Duration;
        }
        return gaps;
    }

    private static void AddGaps(List<TimeRange> gaps, IEnumerable<Clip> clips, double offset, double duration)
    {
        double last = 0;
        foreach (var c in clips.OrderBy(c => c.Start))
        {
            if (c.Start - last > MinimumGap)
                gaps.Add(new TimeRange(offset + last, offset + c.Start));
            last = Math.Max(last, c.End);
        }
        if (duration - last > MinimumGap)
            gaps.Add(new TimeRange(offset + last, offset + duration));
    }

    /// <summary>Everything that will not be exported: uncovered gaps plus excluded clips.</summary>
    public double ExcludedDuration =>
        UncoveredRanges().Sum(g => g.Duration) + Clips.Where(c => !c.IsIncluded).Sum(c => c.Duration);
}
