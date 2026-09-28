using System.Collections.Immutable;

using HighlightCut.Core.Editing;

namespace HighlightCut.Core.Model;

/// <summary>A source range, e.g. an excluded gap between clips.</summary>
public readonly record struct TimeRange(double Start, double End)
{
    public double Duration => End - Start;
}

/// <summary>A stretch of the export: an included clip, or the part of it that earlier clips have not already exported.</summary>
public readonly record struct OutputPart(Clip Clip, double Start, double End)
{
    public double Duration => End - Start;
}

/// <summary>
/// An editing project: one source file and the clips cut from it. The order of <see cref="Clips"/>
/// is the output order. Projects are immutable; edit commands return new instances.
/// </summary>
public sealed record Project(string Name, SourceMedia? Source, ImmutableList<Clip> Clips)
{
    /// <summary>Gaps shorter than this are not reported as excluded ranges.</summary>
    public const double MinimumGap = 0.3;

    public static Project Empty { get; } = new("Untitled project", null, []);

    /// <summary>
    /// Volume and mute of the audio tracks that differ from the default (0 dB, unmuted), by stream index. The mix is
    /// not part of the undo history (see <see cref="EditorSession.SetTrackMix"/>).
    /// </summary>
    public ImmutableList<TrackMix> AudioMix { get; init; } = [];

    /// <summary>The mix of the audio stream with container index <paramref name="index"/>.</summary>
    public TrackMix MixOf(int index) => AudioMix.Find(m => m.Index == index) ?? new TrackMix(index);

    /// <summary>The project with <paramref name="mix"/> for its track; a default mix removes the entry.</summary>
    public Project WithMix(TrackMix mix)
    {
        var list = AudioMix.RemoveAll(m => m.Index == mix.Index);
        if (!mix.IsDefault)
            list = list.Add(mix).Sort((a, b) => a.Index.CompareTo(b.Index));
        return list.SequenceEqual(AudioMix) ? this : this with { AudioMix = list };
    }

    public double SourceDuration => Source?.Duration ?? 0;

    public IEnumerable<Clip> IncludedClips => Clips.Where(c => c.IsIncluded);

    /// <summary>Length of the export: the included clips back to back, each second of the source at most once.</summary>
    public double OutputDuration => OutputParts().Sum(p => p.Duration);

    /// <summary>Overlaps shorter than this (rounding in times from elsewhere) do not count.</summary>
    public const double OverlapTolerance = 1e-6;

    /// <summary>Pieces of output shorter than this are dropped (a thousandth of a second is less than a frame).</summary>
    private const double MinimumPart = 1e-3;

    /// <summary>
    /// What the export plays, in output order. Clips do not overlap, so normally this is every included clip whole. A
    /// project saved before that rule may have clips sharing source time: a clip then loses the seconds an earlier
    /// clip in the output already has (and is left out, or split in two, if that covers it), so nothing is exported
    /// twice.
    /// </summary>
    public IReadOnlyList<OutputPart> OutputParts()
    {
        var parts = new List<OutputPart>();
        var taken = new List<TimeRange>();
        foreach (var clip in IncludedClips)
        {
            double from = clip.Start;
            foreach (var r in taken.Where(r => r.End > clip.Start && r.Start < clip.End).OrderBy(r => r.Start))
            {
                if (r.Start - from >= MinimumPart)
                    parts.Add(new OutputPart(clip, from, r.Start));
                from = Math.Max(from, r.End);
            }
            if (clip.End - from >= MinimumPart)
                parts.Add(new OutputPart(clip, from, clip.End));
            taken.Add(new TimeRange(clip.Start, clip.End));
        }
        return parts;
    }

    /// <summary>
    /// Pairs of clips that share source time, the earlier one first. Edits never create them; projects saved before
    /// that rule can have them.
    /// </summary>
    public IReadOnlyList<(Clip First, Clip Second)> Overlaps()
    {
        var pairs = new List<(Clip, Clip)>();
        var sorted = Clips.OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            for (int j = i + 1; j < sorted.Count && sorted[j].Start < sorted[i].End - OverlapTolerance; j++)
            {
                if (Math.Min(sorted[i].End, sorted[j].End) - sorted[j].Start > OverlapTolerance)
                    pairs.Add((sorted[i], sorted[j]));
            }
        }
        return pairs;
    }

    /// <summary>Seconds of source time shared by two clips, summed over all such pairs.</summary>
    public double OverlapDuration() => Overlaps().Sum(p => Math.Min(p.First.End, p.Second.End) - p.Second.Start);

    /// <summary>The first clip other than <paramref name="exceptId"/> that shares time with the range, if any.</summary>
    public Clip? FirstOverlapping(double start, double end, int? exceptId = null) =>
        Clips.Where(c => c.Id != exceptId && Math.Min(end, c.End) - Math.Max(start, c.Start) > OverlapTolerance)
            .OrderBy(c => c.Start).FirstOrDefault();

    /// <summary>
    /// The part of <paramref name="start"/>–<paramref name="end"/> no clip covers: from <paramref name="start"/> (or,
    /// when a clip covers it, from where that clip ends) to the next clip. Null when less than
    /// <see cref="EditRules.MinClipDuration"/> of it is free.
    /// </summary>
    public TimeRange? FreeRange(double start, double end)
    {
        double from = start;
        foreach (var c in Clips.OrderBy(c => c.Start))
        {
            if (c.Start <= from + OverlapTolerance && c.End > from)
                from = c.End;
        }
        double to = Math.Min(end, Clips.Where(c => c.Start >= from - OverlapTolerance && c.End > from).Select(c => c.Start).DefaultIfEmpty(end).Min());
        return to - from >= EditRules.MinClipDuration - EditRules.Epsilon ? new TimeRange(from, to) : null;
    }

    /// <summary>
    /// How far one end of a clip may move before it runs into another clip: the out-point of the clip before it
    /// (<see cref="ClipEdge.In"/>) or the in-point of the clip after it (<see cref="ClipEdge.Out"/>), or the start or
    /// end of the source. Never past where that end already is, so a clip that overlaps another (an old project) can
    /// shrink but not grow into it.
    /// </summary>
    public double TrimLimit(Clip clip, ClipEdge edge)
    {
        var others = Clips.Where(c => c.Id != clip.Id);
        if (edge == ClipEdge.In)
            return Math.Min(clip.Start, others.Where(c => c.Start < clip.Start).Select(c => c.End).DefaultIfEmpty(0).Max());
        double end = Source?.Duration ?? double.MaxValue;
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

    /// <summary>First clip in output order that contains <paramref name="time"/>.</summary>
    public Clip? ClipAt(double time) => Clips.FirstOrDefault(c => c.Contains(time));

    /// <summary>Source ranges not covered by any clip (included or not), in source order.</summary>
    public IReadOnlyList<TimeRange> UncoveredRanges()
    {
        var gaps = new List<TimeRange>();
        double last = 0;
        foreach (var c in Clips.OrderBy(c => c.Start))
        {
            if (c.Start - last > MinimumGap)
                gaps.Add(new TimeRange(last, c.Start));
            last = Math.Max(last, c.End);
        }
        if (SourceDuration - last > MinimumGap)
            gaps.Add(new TimeRange(last, SourceDuration));
        return gaps;
    }

    /// <summary>Everything that will not be exported: uncovered gaps plus excluded clips.</summary>
    public double ExcludedDuration =>
        UncoveredRanges().Sum(g => g.Duration) + Clips.Where(c => !c.IsIncluded).Sum(c => c.Duration);
}
