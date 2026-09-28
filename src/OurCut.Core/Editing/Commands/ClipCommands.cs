using OurCut.Core.Model;
using OurCut.Core.Time;

namespace OurCut.Core.Editing.Commands;

/// <summary>
/// Adds a clip. Without an index it goes to the end of the output. Its name is <paramref name="Label"/>, or "Clip N"
/// without one, made unique (see <see cref="ClipNames"/>); without a colour it gets the next one of the palette that
/// its neighbours do not have (see <see cref="ClipPalette"/>).
/// </summary>
/// <param name="Id">Explicit id (e.g. to restore a removed clip); by default the next free id.</param>
public sealed record AddClipCommand(double Start, double End, string? Label = null, int? Index = null, int? Id = null, bool IsIncluded = true,
    ClipColor? Color = null) : IEditCommand
{
    public string Name => "add_segment";

    public string Describe(Project before) =>
        $"Added clip {Math.Clamp(Index ?? before.Clips.Count, 0, before.Clips.Count) + 1}";

    public Project Apply(Project project)
    {
        EditRules.ValidateRange(project, Start, End);
        int id = Id ?? project.NextClipId;
        if (project.Find(id) is not null)
            throw new EditException($"Clip {id} already exists.");
        int index = Index ?? project.Clips.Count;
        if (index < 0 || index > project.Clips.Count)
            throw new EditException($"Position {index + 1} is outside the clip list (1–{project.Clips.Count + 1}).");
        var clip = new Clip(id, ClipNames.ForNewClip(project.Clips, id, Label), Start, End, IsIncluded,
            Color ?? ClipPalette.ForNewClip(project.Clips, index, id, Start));
        return project.WithClips(project.Clips.Insert(index, clip));
    }
}

/// <summary>Removes a clip from the project entirely.</summary>
public sealed record RemoveClipCommand(int ClipId) : IEditCommand
{
    public string Name => "remove_segment";

    public string Describe(Project before) => $"Removed clip {before.NumberOf(ClipId)}";

    public Project Apply(Project project)
    {
        var clip = project.Get(ClipId);
        return project.WithClips(project.Clips.Remove(clip));
    }
}

/// <summary>Sets a clip's in- and out-point.</summary>
public sealed record SetClipRangeCommand(int ClipId, double Start, double End) : IEditCommand
{
    public string Name => "trim_segment";

    public string Describe(Project before)
    {
        var clip = before.Get(ClipId);
        int n = before.NumberOf(ClipId);
        bool moveIn = Math.Abs(clip.Start - Start) > EditRules.Epsilon;
        bool moveOut = Math.Abs(clip.End - End) > EditRules.Epsilon;
        return (moveIn, moveOut) switch
        {
            (true, false) => $"Trimmed clip {n} in-point",
            (false, true) => $"Trimmed clip {n} out-point",
            _ => $"Set clip {n} to {TimeFormat.MinutesSeconds(Start)} – {TimeFormat.MinutesSeconds(End)}",
        };
    }

    public Project Apply(Project project)
    {
        var clip = project.Get(ClipId);
        EditRules.ValidateRange(project, Start, End);
        if (clip.Start == Start && clip.End == End)
            return project;
        return project with { Clips = project.Clips.Replace(clip, clip with { Start = Start, End = End }) };
    }
}

/// <summary>
/// Splits a clip in two at a source time. The first part keeps the clip's id, name and colour. The second part is a new
/// clip placed right after it: "Clip N" with the next id, and the next palette colour that differs from the first
/// part's and the following clip's, so the cut stays visible on the timeline.
/// </summary>
public sealed record SplitClipCommand(int ClipId, double At) : IEditCommand
{
    public string Name => "split_segment";

    public string Describe(Project before) => $"Split clip {before.NumberOf(ClipId)} at {TimeFormat.MinutesSeconds(At)}";

    public Project Apply(Project project)
    {
        var clip = project.Get(ClipId);
        if (At < clip.Start + EditRules.MinClipDuration - EditRules.Epsilon || At > clip.End - EditRules.MinClipDuration + EditRules.Epsilon)
            throw new EditException($"Split point must be at least {EditRules.MinClipDuration} s inside clip {project.NumberOf(ClipId)}.");
        var first = clip with { End = At };
        int i = project.IndexOf(ClipId);
        int id = project.NextClipId;
        var clips = project.Clips.SetItem(i, first);
        var second = new Clip(id, ClipNames.ForNewClip(clips, id), At, clip.End, clip.IsIncluded,
            ClipPalette.ForNewClip(clips, i + 1, id, At));
        return project.WithClips(clips.Insert(i + 1, second));
    }

    /// <summary>Id the second part will get when applied to <paramref name="project"/>.</summary>
    public static int SecondPartId(Project project) => project.NextClipId;
}

/// <summary>Includes a clip in the export or excludes it (the clip stays in the project).</summary>
public sealed record SetClipIncludedCommand(int ClipId, bool IsIncluded) : IEditCommand
{
    public string Name => "set_included";

    public string Describe(Project before) =>
        $"{(IsIncluded ? "Kept" : "Excluded")} clip {before.NumberOf(ClipId)}";

    public Project Apply(Project project)
    {
        var clip = project.Get(ClipId);
        return clip.IsIncluded == IsIncluded
            ? project
            : project with { Clips = project.Clips.Replace(clip, clip with { IsIncluded = IsIncluded }) };
    }
}

/// <summary>Moves a clip to another position in the output order (0-based index).</summary>
public sealed record MoveClipCommand(int ClipId, int ToIndex) : IEditCommand
{
    public string Name => "move_segment";

    public string Describe(Project before) => $"Moved clip {before.NumberOf(ClipId)} to position {ToIndex + 1}";

    public Project Apply(Project project)
    {
        var clip = project.Get(ClipId);
        if (ToIndex < 0 || ToIndex >= project.Clips.Count)
            throw new EditException($"Position {ToIndex + 1} is outside the clip list (1–{project.Clips.Count}).");
        int from = project.IndexOf(ClipId);
        return from == ToIndex
            ? project
            : project with { Clips = project.Clips.RemoveAt(from).Insert(ToIndex, clip) };
    }
}

/// <summary>Renames a clip. A name another clip has (ignoring case) is refused: clip names are unique.</summary>
public sealed record RenameClipCommand(int ClipId, string Label) : IEditCommand
{
    public string Name => "set_label";

    public string Describe(Project before) => $"Renamed clip {before.NumberOf(ClipId)} to “{Label.Trim()}”";

    public Project Apply(Project project)
    {
        var clip = project.Get(ClipId);
        if (string.IsNullOrWhiteSpace(Label))
            throw new EditException("Clip names cannot be empty.");
        string label = Label.Trim();
        if (ClipNames.Owner(project.Clips, label, ClipId) is { } other)
            throw new EditException($"Clip {project.NumberOf(other.Id)} is already called “{other.Label}”; clip names must be unique.");
        return clip.Label == label
            ? project
            : project with { Clips = project.Clips.Replace(clip, clip with { Label = label }) };
    }
}

/// <summary>Sets a clip's colour.</summary>
public sealed record SetClipColorCommand(int ClipId, ClipColor Color) : IEditCommand
{
    public string Name => "set_color";

    public string Describe(Project before) => $"Coloured clip {before.NumberOf(ClipId)} {ClipPalette.Key(Color)}";

    public Project Apply(Project project)
    {
        var clip = project.Get(ClipId);
        return clip.Color == Color
            ? project
            : project with { Clips = project.Clips.Replace(clip, clip with { Color = Color }) };
    }
}

/// <summary>Several commands applied as one undo step, e.g. "Removed silence 3×".</summary>
public sealed record BatchCommand(string Name, string Description, IReadOnlyList<IEditCommand> Commands) : IEditCommand
{
    public string Describe(Project before) => Description;

    public Project Apply(Project project)
    {
        foreach (var command in Commands)
            project = command.Apply(project);
        return project;
    }
}

/// <summary>
/// Cuts source ranges (e.g. silences) out of clips: a clip overlapping a range is trimmed or split around it,
/// and removed when nothing of it is left. The first remaining part keeps the clip's id, name and colour; the others
/// are new clips named and coloured like the second part of a split ("Clip N", a colour unlike their neighbours') that
/// follow it in the output. Parts shorter than
/// <see cref="EditRules.MinClipDuration"/> are dropped.
/// </summary>
/// <param name="ClipIds">Clips to cut; every included clip if null.</param>
/// <param name="Name">Command name shown in the history, e.g. <c>cut_silences</c>.</param>
/// <param name="Description">What the edit did, e.g. "Removed 12 silences"; counted from the ranges if null.</param>
public sealed record CutRangesCommand(IReadOnlyList<TimeRange> Ranges, IReadOnlyList<int>? ClipIds = null, string Name = "cut_ranges",
    string? Description = null) : IEditCommand
{
    public string Describe(Project before)
    {
        if (Description is { } description)
            return description;
        double removed = before.OutputDuration - Apply(before).OutputDuration;
        return $"Cut {Ranges.Count} range{(Ranges.Count == 1 ? "" : "s")} ({TimeFormat.ShortDuration(removed)})";
    }

    public Project Apply(Project project)
    {
        if (Ranges.Any(r => !double.IsFinite(r.Start) || !double.IsFinite(r.End)))
            throw new EditException("Range times must be finite numbers.");
        var ranges = Merge(Ranges);
        var targets = ClipIds is null
            ? project.IncludedClips.Select(c => c.Id).ToHashSet()
            : ClipIds.Select(id => project.Get(id).Id).ToHashSet();
        int nextId = project.NextClipId;
        bool changed = false;
        var clips = new List<Clip>();
        for (int k = 0; k < project.Clips.Count; k++)
        {
            var clip = project.Clips[k];
            if (!targets.Contains(clip.Id) || !ranges.Any(r => r.End > clip.Start && r.Start < clip.End))
            {
                clips.Add(clip);
                continue;
            }
            changed = true;
            var parts = Remaining(clip, ranges);
            for (int i = 0; i < parts.Count; i++)
            {
                if (i == 0)
                {
                    clips.Add(clip with { Start = parts[i].Start, End = parts[i].End });
                    continue;
                }
                // Named and coloured against the clips as they are at this point: the new ones so far and the rest.
                var around = clips.Concat(project.Clips.Skip(k + 1)).ToList();
                int id = nextId++;
                clips.Add(new Clip(id, ClipNames.ForNewClip(around, id), parts[i].Start, parts[i].End, clip.IsIncluded,
                    ClipPalette.ForNewClip(around, clips.Count, id, parts[i].Start)));
            }
        }
        return changed ? project.WithClips([.. clips]) : project;
    }

    /// <summary>Sorted ranges with overlapping ones joined.</summary>
    private static List<TimeRange> Merge(IEnumerable<TimeRange> ranges)
    {
        var merged = new List<TimeRange>();
        foreach (var r in ranges.Where(r => r.End > r.Start).OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End)
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, r.End) };
            else
                merged.Add(r);
        }
        return merged;
    }

    /// <summary>The parts of <paramref name="clip"/> outside every range, long enough to keep.</summary>
    private static List<TimeRange> Remaining(Clip clip, List<TimeRange> ranges)
    {
        var parts = new List<TimeRange>();
        double from = clip.Start;
        foreach (var r in ranges)
        {
            if (r.End <= from || r.Start >= clip.End)
                continue;
            if (r.Start > from)
                parts.Add(new TimeRange(from, r.Start));
            from = Math.Max(from, r.End);
        }
        if (clip.End > from)
            parts.Add(new TimeRange(from, clip.End));
        return [.. parts.Where(p => p.Duration >= EditRules.MinClipDuration - EditRules.Epsilon)];
    }
}
