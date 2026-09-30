using System.Globalization;
using System.Text;

namespace HighlightCut.App.Services;

/// <summary>
/// A bullet: <c>**Clips snap together.** Trim a clip…</c> is the bold <see cref="Lead"/> "Clips snap together." and
/// the <see cref="Text"/> after it (with its leading space). A bullet without a bold start is all text.
/// </summary>
public sealed record ChangelogItem(string Lead, string Text)
{
    public bool HasLead => Lead.Length > 0;

    public static ChangelogItem Parse(string markdown)
    {
        if (markdown.StartsWith("**", StringComparison.Ordinal))
        {
            int end = markdown.IndexOf("**", 2, StringComparison.Ordinal);
            if (end > 2)
                return new ChangelogItem(markdown[2..end].Trim(), markdown[(end + 2)..]);
        }
        return new ChangelogItem("", markdown);
    }

    /// <summary>The bullet as CHANGELOG.md has it.</summary>
    public override string ToString() => HasLead ? $"**{Lead}**{Text}" : Text;
}

/// <summary>A heading of a release's notes (New, Improved, Fixed) and its bullets.</summary>
public sealed record ChangelogGroup(string Title, IReadOnlyList<ChangelogItem> Items);

/// <summary>
/// One <c>## X.Y.Z — date — Codename 🎬</c> section of CHANGELOG.md, or <c>## Unreleased</c> (no version). The date and
/// the codename may be missing.
/// </summary>
public sealed record ChangelogRelease(string Heading, Version? Version, string? Date, string? Codename, IReadOnlyList<ChangelogGroup> Groups)
{
    public bool IsEmpty => Groups.All(g => g.Items.Count == 0);

    /// <summary>The codename without its emoji ("First Cut ✂️" is "First Cut"), which not every font has.</summary>
    public string? Name => Codename is null ? null : Changelog.WithoutEmoji(Codename) is { Length: > 0 } name ? name : null;

    /// <summary>The heading without emoji, as What's new shows it: "0.1.0 — 2026-09-30 — First Cut".</summary>
    public string Label => Version is null ? Heading : string.Join(" — ", new[] { Version.ToString(3), Date, Name }.Where(p => p is not null));
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
    /// Reads the <c>## </c> sections, their <c>### </c> groups and <c>- </c> bullets (a bold start is the bullet's lead);
    /// an indented line continues the bullet above. Anything before the first section, and bullets outside a group, are left out.
    /// </summary>
    public static Changelog Parse(string markdown)
    {
        var releases = new List<ChangelogRelease>();
        string? heading = null;
        List<(string Title, List<string> Items)> groups = [];
        List<string>? items = null;

        void EndRelease()
        {
            if (heading is null)
                return;
            // "0.1.0 — 2026-09-30 — First Cut ✂️": the version, the date and the codename, after dashes (em or en).
            string[] parts = heading.Split(['—', '–'], 3, StringSplitOptions.TrimEntries);
            var version = AppVersion.ReleaseOf(parts[0]);
            releases.Add(new ChangelogRelease(heading, version, Part(parts, 1), Part(parts, 2),
                [.. groups.Select(g => new ChangelogGroup(g.Title, [.. g.Items.Select(ChangelogItem.Parse)]))]));
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
                groups.Add((line[4..].Trim(), items));
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

    private static string? Part(string[] parts, int index) => parts.Length > index && parts[index].Length > 0 ? parts[index] : null;

    /// <summary>The text without emoji and the joiners and variation selectors that go with them.</summary>
    public static string WithoutEmoji(string text)
    {
        var kept = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is not (UnicodeCategory.OtherSymbol or UnicodeCategory.Format
                    or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark))
                kept.Append(rune.ToString());
        }
        return string.Join(' ', kept.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
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
