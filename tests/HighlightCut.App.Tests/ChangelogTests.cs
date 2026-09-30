using HighlightCut.App.Services;

namespace HighlightCut.App.Tests;

/// <summary>CHANGELOG.md: how it is read, which sections What's new shows, and that this version has notes.</summary>
public class ChangelogTests
{
    private const string Sample = """
        # Changelog

        Text before the first section is left out.

        ## Unreleased

        ### New

        - Something not released yet.

        ## 0.3.0 — 2026-12-01 — Chapters 📖

        ### New

        - **Chapters.** In the export.
        - A long line that goes on
          onto the next one.

        ### Fixed

        - Undo after a split.

        ## 0.2.0 – 2026-11-01

        ### Improved

        * Faster opening.

        ## 0.1.0

        - A bullet outside a group is left out.
        """;

    [Fact]
    public void Reads_sections_groups_and_bullets()
    {
        var log = Changelog.Parse(Sample.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(["Unreleased", "0.3.0 — 2026-12-01 — Chapters 📖", "0.2.0 – 2026-11-01", "0.1.0"], log.Releases.Select(r => r.Heading));
        Assert.Null(log.Releases[0].Version);
        Assert.Equal("Unreleased", log.Releases[0].Label);
        var v3 = log.Releases[1];
        Assert.Equal(new Version(0, 3, 0), v3.Version);
        Assert.Equal("2026-12-01", v3.Date);
        Assert.Equal("Chapters 📖", v3.Codename);
        Assert.Equal("Chapters", v3.Name);
        Assert.Equal("0.3.0 — 2026-12-01 — Chapters", v3.Label);
        Assert.Equal(["New", "Fixed"], v3.Groups.Select(g => g.Title));
        Assert.Equal([new ChangelogItem("Chapters.", " In the export."), new ChangelogItem("", "A long line that goes on onto the next one.")], v3.Groups[0].Items);
        Assert.Equal("**Chapters.** In the export.", v3.Groups[0].Items[0].ToString());
        Assert.Equal(["Undo after a split."], v3.Groups[1].Items.Select(i => i.Text));
        Assert.Equal("2026-11-01", log.Releases[2].Date);
        Assert.Null(log.Releases[2].Codename);
        Assert.Equal("0.2.0 — 2026-11-01", log.Releases[2].Label);
        Assert.Equal(["Faster opening."], log.Releases[2].Groups[0].Items.Select(i => i.Text));
        Assert.Null(log.Releases[3].Date);
        Assert.True(log.Releases[3].IsEmpty);
    }

    [Fact]
    public void Since_lists_the_newer_sections_up_to_the_current_version()
    {
        var log = Changelog.Parse(Sample);

        Assert.Equal(["0.3.0", "0.2.0"], log.Since(new Version(0, 1, 0), new Version(0, 3, 0)).Select(r => r.Version!.ToString(3)));
        Assert.Equal(["0.2.0"], log.Since(new Version(0, 1, 0), new Version(0, 2, 0)).Select(r => r.Version!.ToString(3)));
        Assert.Empty(log.Since(new Version(0, 3, 0), new Version(0, 3, 0)));
        // Never saw a version: only the current one's notes.
        Assert.Equal(["0.3.0"], log.Since(null, new Version(0, 3, 0)).Select(r => r.Version!.ToString(3)));
        // Empty sections are not shown.
        Assert.Empty(log.Since(null, new Version(0, 1, 0)));
        Assert.Empty(Changelog.Parse("").Releases);
    }

    [Theory]
    [InlineData("**Clips snap.** Trim a clip.", "Clips snap.", " Trim a clip.")]
    [InlineData("**Bold only.**", "Bold only.", "")]
    [InlineData("No lead, **bold** later.", "", "No lead, **bold** later.")]
    [InlineData("**Not closed.", "", "**Not closed.")]
    [InlineData("****", "", "****")]
    public void A_bullet_has_a_bold_lead(string markdown, string lead, string text)
    {
        var item = ChangelogItem.Parse(markdown);
        Assert.Equal(lead, item.Lead);
        Assert.Equal(text, item.Text);
        Assert.Equal(markdown, item.ToString());
    }

    [Theory]
    [InlineData("First Cut ✂️", "First Cut")]
    [InlineData("Unblocked 🔓", "Unblocked")]
    [InlineData("Family 👨‍👩‍👧 Time", "Family Time")]
    [InlineData("Plain", "Plain")]
    public void Emoji_are_left_out_of_the_codename(string codename, string name) => Assert.Equal(name, Changelog.WithoutEmoji(codename));

    [Theory]
    [InlineData("0.1.0+4f2a9c1", "0.1.0")]
    [InlineData("0.1.0-dev.42+4f2a9c1", "0.1.0-dev.42")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData(null, "")]
    public void Version_text_drops_the_commit(string? informational, string expected) => Assert.Equal(expected, AppVersion.Clean(informational));

    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0-dev.42", "0.1.0")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", null)]
    [InlineData("1.2.3.4", null)]
    [InlineData("Unreleased", null)]
    [InlineData(null, null)]
    public void Release_is_the_x_y_z_part(string? text, string? expected) => Assert.Equal(expected, AppVersion.ReleaseOf(text)?.ToString(3));

    [Fact]
    public void This_version_has_notes()
    {
        // Version in Directory.Build.props: bumping it without a CHANGELOG.md section fails here.
        Assert.Matches(@"^\d+\.\d+\.\d+", AppVersion.Text);
        var notes = Changelog.Embedded.Find(AppVersion.Release);
        Assert.True(notes is { IsEmpty: false }, $"CHANGELOG.md has no notes under ## {AppVersion.Release.ToString(3)}.");
        Assert.NotNull(notes!.Date);
        Assert.NotNull(notes.Name);
        Assert.Equal("Unreleased", Changelog.Embedded.Releases[0].Heading);
        // Every bullet starts with a bold lead, which What's new shows in bold.
        Assert.All(Changelog.Embedded.Releases.SelectMany(r => r.Groups).SelectMany(g => g.Items), i => Assert.True(i.HasLead, i.Text));
    }

    [Fact]
    public void The_ukrainian_notes_mirror_the_english_ones()
    {
        // scripts/release-notes.py checks the same in the Changelog workflow; this catches it in the tests too.
        var uk = Changelog.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "CHANGELOG.uk.md")));
        var en = Changelog.Embedded;
        Assert.Equal(en.Releases.Select(r => r.Heading), uk.Releases.Select(r => r.Heading));
        var names = new Dictionary<string, string> { ["New"] = "Нове", ["Improved"] = "Покращено", ["Fixed"] = "Виправлено" };
        foreach (var (e, u) in en.Releases.Zip(uk.Releases))
        {
            Assert.Equal(e.Groups.Select(g => (names[g.Title], g.Items.Count)), u.Groups.Select(g => (g.Title, g.Items.Count)));
            Assert.All(u.Groups.SelectMany(g => g.Items), i => Assert.True(i.HasLead, i.Text));
        }
    }

    [Fact]
    public void The_app_has_the_repository_changelog()
    {
        var file = Changelog.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "CHANGELOG.md")));
        Assert.Equal(file.Releases.Select(r => r.Heading), Changelog.Embedded.Releases.Select(r => r.Heading));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HighlightCut.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
