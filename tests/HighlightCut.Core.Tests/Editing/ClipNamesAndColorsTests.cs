using HighlightCut.Core.Editing;
using HighlightCut.Core.Editing.Commands;
using HighlightCut.Core.Model;
using HighlightCut.Core.Serialization;

namespace HighlightCut.Core.Tests.Editing;

public class ClipNamesTests
{
    private static EditorSession Session() => new(Sample.Project);

    private static void AssertUnique(Project project) =>
        Assert.Equal(project.Clips.Count, project.Clips.Select(c => c.Label.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    [Fact]
    public void Splitting_twice_gives_every_part_its_own_name()
    {
        var session = Session();
        var second = session.Split(3, 300);
        var third = session.Split(second.Id, 350);
        var fourth = session.Split(3, 270);

        Assert.Equal(["Demo — import", "Clip 9", "Clip 7", "Clip 8"],
            session.Project.Clips.Skip(2).Take(4).Select(c => c.Label));
        Assert.Equal(("Clip 7", "Clip 8", "Clip 9"), (second.Label, third.Label, fourth.Label));
        AssertUnique(session.Project);
    }

    [Fact]
    public void A_deleted_clips_number_is_never_handed_out_again()
    {
        var session = Session();
        var added = session.AddClip(100, 110);
        Assert.Equal("Clip 7", added.Label);

        session.Remove(added.Id);
        Assert.Equal(7, session.Project.LastClipId);
        var next = session.AddClip(100, 110);
        Assert.Equal((8, "Clip 8"), (next.Id, next.Label));

        // Removing the clip with the highest id of a project that never counted (an old file) does not free it either.
        var old = Sample.Project with { LastClipId = 0 };
        var after = new RemoveClipCommand(6).Apply(old);
        Assert.Equal(7, after.NextClipId);
    }

    [Fact]
    public void Undo_takes_the_counter_back_with_the_clip()
    {
        var session = Session();
        session.AddClip(100, 110);
        session.Undo();
        Assert.Null(session.Project.Find(7));
        // The undone clip never happened: the number is free again, and only one clip ever has it.
        Assert.Equal("Clip 7", session.AddClip(100, 110).Label);
        AssertUnique(session.Project);
    }

    [Fact]
    public void Cut_parts_get_the_next_numbers()
    {
        var after = new CutRangesCommand([new(1, 20, 22), new(1, 30, 32), new(1, 150, 152)]).Apply(Sample.Project);

        Assert.Equal(["Intro", "Clip 7", "Clip 8", "Setup", "Clip 9"], after.Clips.Take(5).Select(c => c.Label));
        Assert.Equal(9, after.LastClipId);
        AssertUnique(after);
    }

    [Fact]
    public void A_given_name_that_is_taken_gets_a_counter()
    {
        var session = Session();
        Assert.Equal("Intro · 2", session.AddClip(100, 110, "Intro").Label);
        Assert.Equal("intro · 3", session.AddClip(110, 115, " intro ").Label);
        // The name is kept as given, with the counter added.
        Assert.Equal("OUTRO · 2", session.AddClip(850, 860, "OUTRO").Label);
        AssertUnique(session.Project);
    }

    [Fact]
    public void An_unnamed_clip_skips_a_number_someone_took_as_a_name()
    {
        var session = Session();
        session.Rename(1, "Clip 7");
        Assert.Equal("Clip 7 · 2", session.AddClip(100, 110).Label);
        AssertUnique(session.Project);
    }

    [Fact]
    public void Renaming_to_another_clips_name_is_refused()
    {
        var session = Session();
        var e = Assert.Throws<EditException>(() => session.Rename(2, " intro "));
        Assert.Equal("Clip 1 is already called “Intro”; clip names must be unique.", e.Message);
        Assert.Equal("Setup", session.Project.Get(2).Label);

        // A clip can change the case of its own name.
        session.Rename(1, "INTRO");
        Assert.Equal("INTRO", session.Project.Get(1).Label);
    }

    [Fact]
    public void A_batch_with_a_clashing_rename_changes_nothing()
    {
        var session = Session();
        var batch = new BatchCommand("set_label", "Renamed", [new RenameClipCommand(1, "Opening"), new RenameClipCommand(2, "Opening")]);
        Assert.Throws<EditException>(() => session.Execute(batch));
        Assert.Same(Sample.Project, session.Project);
    }

    [Fact]
    public void Reverting_a_rename_whose_old_name_was_taken_since_is_refused()
    {
        var session = Session();
        var rename = session.Execute(new RenameClipCommand(1, "Opening"))!;
        session.Rename(2, "Intro");

        var e = Assert.Throws<EditException>(() => session.Revert(rename));
        Assert.Contains("“Intro”", e.Message, StringComparison.Ordinal);
    }
}

public class ClipColorTests
{
    private static void AssertNeighboursDiffer(Project project)
    {
        for (int i = 1; i < project.Clips.Count; i++)
            Assert.NotEqual(project.Clips[i - 1].Color, project.Clips[i].Color);
    }

    [Fact]
    public void New_clips_take_the_palette_in_turn()
    {
        var session = new EditorSession(new Project("p", Sample.Source, []));
        for (int i = 0; i < 12; i++)
            session.AddClip(i * 10, i * 10 + 5);

        Assert.Equal([.. ClipPalette.Colors, ClipPalette.Colors[0], ClipPalette.Colors[1]], session.Project.Clips.Select(c => c.Color));
    }

    [Fact]
    public void A_new_clip_never_matches_its_neighbours()
    {
        // Clip 7 would be orange; place it between two orange clips and it takes the next colour.
        var project = new Project("p", Sample.Source,
            [new Clip(1, "A", 0, 10, Color: ClipColor.Orange), new Clip(2, "B", 20, 30, Color: ClipColor.Orange)]) { LastClipId = 6 };
        var after = new AddClipCommand(12, 18, Index: 1).Apply(project);

        Assert.Equal(ClipColor.Indigo, after.Clips[1].Color);
    }

    [Fact]
    public void Neighbours_on_the_timeline_count_too()
    {
        // Added at the end of the output, but on the timeline it sits between A and B.
        var project = new Project("p", Sample.Source,
            [new Clip(1, "A", 0, 10, Color: ClipColor.Orange), new Clip(2, "B", 40, 50, Color: ClipColor.Indigo),
             new Clip(3, "C", 60, 70, Color: ClipColor.Teal)]) { LastClipId = 6 };
        var after = new AddClipCommand(20, 30).Apply(project);

        Assert.Equal(ClipColor.Emerald, after.Clips[^1].Color);
    }

    [Fact]
    public void Split_halves_get_different_colours()
    {
        var session = new EditorSession(Sample.Project);
        int id = 3;
        for (int k = 0; k < 8; k++)
            id = session.Split(id, 250 + k * 15).Id;

        var parts = session.Project.Clips.Where(c => c.Start >= 242.88 && c.End <= 404).ToList();
        Assert.Equal(9, parts.Count);
        Assert.Equal(ClipColor.Violet, parts[0].Color);
        AssertNeighboursDiffer(session.Project);
    }

    [Fact]
    public void Silence_cuts_colour_every_new_part_apart_from_its_neighbours()
    {
        var pauses = Enumerable.Range(0, 20).Select(k => new SourceRange(1, 250 + k * 7, 251 + k * 7)).ToList();
        var after = new CutRangesCommand(pauses).Apply(Sample.Project);

        Assert.True(after.Clips.Count > 20);
        AssertNeighboursDiffer(after);
    }

    [Fact]
    public void The_colour_can_be_changed_and_undone()
    {
        var session = new EditorSession(Sample.Project);
        var entry = session.Execute(new SetClipColorCommand(2, ClipColor.Pink))!;

        Assert.Equal(ClipColor.Pink, session.Project.Get(2).Color);
        Assert.Equal("Coloured clip 2 pink", entry.Description);
        Assert.Null(session.Execute(new SetClipColorCommand(2, ClipColor.Pink)));

        session.Undo();
        Assert.Equal(ClipColor.Amber, session.Project.Get(2).Color);
        session.Redo();
        Assert.Equal(ClipColor.Pink, session.Project.Get(2).Color);
    }

    [Fact]
    public void Colour_names_are_read_in_any_case_and_numbers_are_not_names()
    {
        Assert.True(ClipPalette.TryParse(" Teal ", out var teal));
        Assert.Equal(ClipColor.Teal, teal);
        Assert.False(ClipPalette.TryParse("3", out _));
        Assert.False(ClipPalette.TryParse("blue", out _));
        Assert.False(ClipPalette.TryParse(null, out _));
        Assert.All(ClipPalette.Colors, c => Assert.Matches("^#[0-9A-F]{6}$", ClipPalette.Hex(c)));
        Assert.Equal(ClipPalette.Colors.Count, ClipPalette.Colors.Select(ClipPalette.Hex).Distinct().Count());
    }
}

public class ClipFileTests
{
    [Fact]
    public void Colours_and_the_clip_counter_round_trip()
    {
        var session = new EditorSession(Sample.Project);
        session.SetColor(1, ClipColor.Pink);
        session.Remove(session.AddClip(100, 110).Id);

        string json = ProjectFile.Serialize(session.Project);
        Assert.Contains("\"color\": \"pink\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lastClipId\": 7", json, StringComparison.Ordinal);

        var back = ProjectFile.Deserialize(json);
        Assert.Equal(session.Project.Clips, back.Clips);
        Assert.Equal(8, back.NextClipId);
    }

    [Fact]
    public void Old_files_keep_their_names_and_get_colours()
    {
        const string json = """
            {
              "format": "highlightcut-project", "version": 1,
              "clips": [
                { "id": 1, "label": "Intro", "start": 0, "end": 10 },
                { "id": 3, "label": "Intro (b)", "start": 10, "end": 20 },
                { "id": 2, "label": "Talk", "start": 30, "end": 40, "color": "mauve" },
                { "id": 4, "label": "Talk", "start": 50, "end": 60 },
                { "id": 5, "label": "talk ", "start": 60, "end": 70, "color": "lime" }
              ]
            }
            """;
        var p = ProjectFile.Deserialize(json);

        // The first clip with a name keeps it; later ones with the same name get a counter.
        Assert.Equal(["Intro", "Intro (b)", "Talk", "Talk · 2", "talk · 3"], p.Clips.Select(c => c.Label));
        Assert.Equal(ClipColor.Lime, p.Clips[4].Color);
        for (int i = 1; i < p.Clips.Count; i++)
            Assert.NotEqual(p.Clips[i - 1].Color, p.Clips[i].Color);
        // Without a counter in the file, the next id follows the highest one.
        Assert.Equal(6, p.NextClipId);
    }

    [Fact]
    public void A_counter_in_the_file_below_the_ids_is_raised()
    {
        const string json = """
            { "format": "highlightcut-project", "version": 1, "lastClipId": 2, "clips": [ { "id": 5, "start": 0, "end": 1, "color": "cyan" } ] }
            """;
        var p = ProjectFile.Deserialize(json);
        Assert.Equal(5, p.LastClipId);
        Assert.Equal((ClipColor.Cyan, "Clip 5"), (p.Clips[0].Color, p.Clips[0].Label));
    }
}
