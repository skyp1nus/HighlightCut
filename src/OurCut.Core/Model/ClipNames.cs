namespace OurCut.Core.Model;

/// <summary>
/// Clip names. Every clip in a project has its own name (compared ignoring case and surrounding spaces):
/// <list type="bullet">
/// <item>A new clip without a name, including the second half of a split and the extra parts of a cut, is called
/// "Clip N", where N is its id. Ids come from <see cref="Project.LastClipId"/>, which only goes up, so a number is never
/// handed out twice in a project, even after its clip is deleted.</item>
/// <item>A new clip given a name that is taken (by "Keep as clip", Claude or a file) gets " · 2", " · 3"… added.</item>
/// <item>Renaming a clip to a name another clip has is refused.</item>
/// </list>
/// </summary>
public static class ClipNames
{
    /// <summary>Joins a name and its counter: "Intro · 2".</summary>
    public const string Separator = " · ";

    /// <summary>The name a new clip gets when none is given.</summary>
    public static string Default(int id) => $"Clip {id}";

    public static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The clip other than <paramref name="exceptId"/> called <paramref name="label"/>, if any.</summary>
    public static Clip? Owner(IEnumerable<Clip> clips, string label, int? exceptId = null) =>
        clips.FirstOrDefault(c => c.Id != exceptId && Same(c.Label, label));

    /// <summary>
    /// <paramref name="preferred"/> (trimmed) if no clip other than <paramref name="exceptId"/> has it, otherwise the
    /// first free "<paramref name="preferred"/> · 2", " · 3"….
    /// </summary>
    public static string Unique(IEnumerable<Clip> clips, string preferred, int? exceptId = null)
    {
        string name = preferred.Trim();
        var taken = clips.Where(c => c.Id != exceptId).Select(c => c.Label.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
            return name;
        for (int k = 2; ; k++)
        {
            string candidate = name + Separator + k;
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>The name for new clip <paramref name="id"/>: <paramref name="label"/> if given, else "Clip N"; made unique.</summary>
    public static string ForNewClip(IEnumerable<Clip> clips, int id, string? label = null) =>
        Unique(clips, string.IsNullOrWhiteSpace(label) ? Default(id) : label);
}
