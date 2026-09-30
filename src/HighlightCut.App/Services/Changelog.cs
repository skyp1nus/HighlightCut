namespace HighlightCut.App.Services;

/// <summary>A heading of a release's notes (New, Improved, Fixed) and its bullets.</summary>
public sealed record ChangelogGroup(string Title, IReadOnlyList<string> Items);

/// <summary>One <c>## X.Y.Z — date</c> section of CHANGELOG.md, or <c>## Unreleased</c> (no version).</summary>
public sealed record ChangelogRelease(string Heading, Version? Version, string? Date, IReadOnlyList<ChangelogGroup> Groups)
{
    public bool IsEmpty => Groups.All(g => g.Items.Count == 0);
}

/// <summary>
/// CHANGELOG.md at the repository root, embedded in the app: the notes of every release, newest first. What's new
/// shows the sections newer than the version the user saw last; the release job of CI publishes one section.
/// </summary>
public sealed class Changelog(IReadOnlyList<ChangelogRelease> releases)
{
    /// <summary>The copy built into the app.</summary>
    public static Changelog Embedded { get; } = LoadEmbedded();

    public IReadOnlyList<ChangelogRelease> Releases { get; } = releases;

    /// <summary>The section of a version (X.Y.Z); null when it has none.</summary>
    public ChangelogRelease? Find(Version version) => Releases.FirstOrDefault(r => r.Version == version);

    /// <summary>
    /// What's new since <paramref name="lastSeen"/>, newest first: the sections after it, up to <paramref name="current"/>.
    /// Without a last seen version, only the current version's section.
    /// </summary>
    public IReadOnlyList<ChangelogRelease> Since(Version? lastSeen, Version current) =>
    [
        .. Releases.Where(r => r.Version is { } v && v <= current && (lastSeen is null ? v == current : v > lastSeen) && !r.IsEmpty)
            .OrderByDescending(r => r.Version),
    ];

    /// <summary>
    /// Reads the <c>## </c> sections, their <c>### </c> groups and <c>- </c> bullets; an indented line continues the
    /// bullet above. Anything before the first section, and bullets outside a group, are left out.
    /// </summary>
    public static Changelog Parse(string markdown)
    {
        var releases = new List<ChangelogRelease>();
        string? heading = null;
        List<ChangelogGroup> groups = [];
        List<string>? items = null;

        void EndRelease()
        {
            if (heading is null)
                return;
            // "0.1.0 — 2026-09-30": the version, then the date after a dash (em or en).
            string[] parts = heading.Split(['—', '–'], 2, StringSplitOptions.TrimEntries);
            var version = AppVersion.ReleaseOf(parts[0]);
            releases.Add(new ChangelogRelease(heading, version, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null, groups));
        }

        foreach (string raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                EndRelease();
                heading = line[3..].Trim();
                groups = [];
                items = null;
            }
            else if (heading is null)
            {
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                items = [];
                groups.Add(new ChangelogGroup(line[4..].Trim(), items));
            }
            else if (items is not null && (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)))
            {
                items.Add(line[2..].Trim());
            }
            else if (items is { Count: > 0 } && line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                items[^1] += " " + line.Trim();
            }
        }
        EndRelease();
        return new Changelog(releases);
    }

    private static Changelog LoadEmbedded()
    {
        using var stream = typeof(Changelog).Assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null)
            return new Changelog([]);
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
