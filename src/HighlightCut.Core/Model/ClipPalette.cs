namespace HighlightCut.Core.Model;

/// <summary>
/// The clip colours. They are ordered so that neighbours in the list are far apart on the colour wheel: new clips take
/// them in this order. None of them is the accent blue, which stays for the playhead and the selected clip.
/// </summary>
public enum ClipColor
{
    Teal,
    Amber,
    Violet,
    Rose,
    Lime,
    Cyan,
    Orange,
    Indigo,
    Emerald,
    Pink,
}

/// <summary>The clip colours, their values and how new clips get one.</summary>
public static class ClipPalette
{
    public static IReadOnlyList<ClipColor> Colors { get; } = Enum.GetValues<ClipColor>();

    /// <summary>
    /// The colour as #RRGGBB. The 400 shades of Tailwind's palette: light enough to read on the matte-black panels,
    /// and kept away from the red of destructive actions and the green of status dots.
    /// </summary>
    public static string Hex(ClipColor color) => color switch
    {
        ClipColor.Teal => "#2DD4BF",
        ClipColor.Amber => "#FBBF24",
        ClipColor.Violet => "#A78BFA",
        ClipColor.Rose => "#FB7185",
        ClipColor.Lime => "#A3E635",
        ClipColor.Cyan => "#22D3EE",
        ClipColor.Orange => "#FB923C",
        ClipColor.Indigo => "#818CF8",
        ClipColor.Emerald => "#34D399",
        ClipColor.Pink => "#F472B6",
        _ => throw new ArgumentOutOfRangeException(nameof(color)),
    };

    /// <summary>The name used in project files and by Claude, e.g. "teal".</summary>
    public static string Key(ClipColor color) => color.ToString().ToLowerInvariant();

    /// <summary>Reads a colour name (any case), e.g. "Teal".</summary>
    public static bool TryParse(string? name, out ClipColor color) =>
        Enum.TryParse(name?.Trim(), ignoreCase: true, out color) && Enum.IsDefined(color) && !int.TryParse(name, out _);

    /// <summary>
    /// The colour for a new clip with id <paramref name="id"/> inserted at <paramref name="index"/> of
    /// <paramref name="clips"/> (the output order), starting at <paramref name="start"/>: the palette in turn by id,
    /// skipping the colours of its neighbours in the output order and on the timeline.
    /// </summary>
    public static ClipColor ForNewClip(IReadOnlyList<Clip> clips, int index, int id, double start)
    {
        var avoid = new HashSet<ClipColor>();
        if (index > 0 && index <= clips.Count)
            avoid.Add(clips[index - 1].Color);
        if (index >= 0 && index < clips.Count)
            avoid.Add(clips[index].Color);
        foreach (var c in TimelineNeighbours(clips, id, start))
            avoid.Add(c.Color);
        return Next(id, avoid);
    }

    /// <summary>The palette colour for <paramref name="id"/>, or the next one after it that is not in <paramref name="avoid"/>.</summary>
    public static ClipColor Next(int id, IReadOnlySet<ClipColor> avoid)
    {
        int n = Colors.Count;
        int first = ((id - 1) % n + n) % n;
        for (int k = 0; k < n; k++)
        {
            var color = Colors[(first + k) % n];
            if (!avoid.Contains(color))
                return color;
        }
        return Colors[first];
    }

    /// <summary>
    /// Colours the clips in <paramref name="uncoloured"/> (e.g. from a file saved before clips had colours) the way new
    /// clips are coloured, in output order, so no two neighbours match. The other clips keep theirs.
    /// </summary>
    public static IReadOnlyList<Clip> Fill(IReadOnlyList<Clip> clips, IReadOnlySet<int> uncoloured)
    {
        var result = clips.ToList();
        var pending = new HashSet<int>(uncoloured);
        for (int i = 0; i < result.Count; i++)
        {
            var clip = result[i];
            if (!pending.Contains(clip.Id))
                continue;
            var avoid = new HashSet<ClipColor>();
            if (i > 0)
                avoid.Add(result[i - 1].Color);
            if (i + 1 < result.Count && !pending.Contains(result[i + 1].Id))
                avoid.Add(result[i + 1].Color);
            foreach (var c in TimelineNeighbours(result.Where(c => !pending.Contains(c.Id)).ToList(), clip.Id, clip.Start))
                avoid.Add(c.Color);
            result[i] = clip with { Color = Next(clip.Id, avoid) };
            pending.Remove(clip.Id);
        }
        return result;
    }

    /// <summary>
    /// The clips right before and after a range on the source timeline: the one that starts last before it and the one
    /// that starts first after it.
    /// </summary>
    private static IEnumerable<Clip> TimelineNeighbours(IReadOnlyList<Clip> clips, int id, double start)
    {
        Clip? before = null, after = null;
        foreach (var c in clips.Where(c => c.Id != id))
        {
            if (c.Start < start && (before is null || c.Start > before.Start))
                before = c;
            else if (c.Start >= start && (after is null || c.Start < after.Start))
                after = c;
        }
        if (before is not null)
            yield return before;
        if (after is not null)
            yield return after;
    }
}
