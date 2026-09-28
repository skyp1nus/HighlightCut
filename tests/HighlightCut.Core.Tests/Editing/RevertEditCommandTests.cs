using HighlightCut.Core.Editing;
using HighlightCut.Core.Editing.Commands;
using HighlightCut.Core.Model;

namespace HighlightCut.Core.Tests.Editing;

public class RevertEditCommandTests
{
    private static EditorSession Session() => new(Sample.Project);

    [Fact]
    public void Reverting_an_earlier_trim_keeps_later_edits()
    {
        var s = Session();
        var trim = s.Execute(new SetClipRangeCommand(2, 123.14, 190.12), EditOrigin.Assistant)!;
        s.SetRange(5, 750, 812.4, EditOrigin.Assistant);

        var entry = s.Revert(trim)!;

        Assert.Equal(118.6, s.Project.Get(2).Start);
        Assert.Equal(812.4, s.Project.Get(5).End);
        Assert.Equal("revert_action", entry.Command.Name);
        Assert.Equal("Reverted “Trimmed clip 2 in-point”", entry.Description);
        Assert.True(s.IsReverted(trim));
    }

    [Fact]
    public void A_revert_is_an_edit_that_can_be_undone_and_redone()
    {
        var s = Session();
        var trim = s.Execute(new SetClipRangeCommand(2, 123.14, 190.12))!;
        s.Revert(trim);

        s.Undo();
        Assert.Equal(123.14, s.Project.Get(2).Start);
        Assert.False(s.IsReverted(trim));
        s.Redo();
        Assert.Equal(118.6, s.Project.Get(2).Start);
        Assert.True(s.IsReverted(trim));
    }

    [Fact]
    public void Reverting_an_added_clip_removes_it_and_a_removal_puts_the_clip_back_in_place()
    {
        var s = Session();
        var add = s.Execute(new AddClipCommand(432, 460, "Key quote", Index: 3))!;
        var remove = s.Execute(new RemoveClipCommand(2))!;

        s.Revert(add);
        Assert.Null(s.Project.Find(7));
        Assert.Null(s.Project.Find(2));

        s.Revert(remove);
        Assert.Equal(Sample.Project.Clips, s.Project.Clips);
    }

    [Fact]
    public void Reverting_a_batch_restores_every_clip_it_changed()
    {
        var s = Session();
        var batch = s.Execute(new BatchCommand("trim_segment", "Removed 3 silences",
        [
            new SetClipRangeCommand(1, 12.04, 40.8),
            new SetClipRangeCommand(2, 124, 190.12),
            new SetClipRangeCommand(5, 750, 822),
        ]))!;
        s.Rename(3, "Export demo");

        s.Revert(batch);

        Assert.Equal(Sample.Project.Get(1), s.Project.Get(1));
        Assert.Equal(Sample.Project.Get(2), s.Project.Get(2));
        Assert.Equal(Sample.Project.Get(5), s.Project.Get(5));
        Assert.Equal("Export demo", s.Project.Get(3).Label);
    }

    [Fact]
    public void Reverting_a_move_restores_the_order()
    {
        var s = Session();
        var move = s.Execute(new MoveClipCommand(1, 3))!;
        s.SetIncluded(4, false);

        s.Revert(move);

        Assert.Equal([1, 2, 3, 4, 6, 5], s.Project.Clips.Select(c => c.Id));
        Assert.False(s.Project.Get(4).IsIncluded);
    }

    [Fact]
    public void Reverting_the_revert_brings_the_edit_back()
    {
        var s = Session();
        var trim = s.Execute(new SetClipRangeCommand(2, 123.14, 190.12))!;
        var revert = s.Revert(trim)!;
        Assert.Same(revert, s.RevertOf(trim));

        s.Revert(revert);

        Assert.Equal(123.14, s.Project.Get(2).Start);
        Assert.False(s.IsReverted(trim));
        Assert.True(s.IsReverted(revert));
    }

    [Fact]
    public void Later_changes_to_the_same_clip_are_a_conflict()
    {
        var s = Session();
        var trim = s.Execute(new SetClipRangeCommand(2, 123.14, 190.12))!;
        s.SetRange(2, 123.14, 180);

        var e = Assert.Throws<EditException>(() => s.Revert(trim));
        Assert.Contains("was changed after", e.Message);
        Assert.Equal(180, s.Project.Get(2).End);
    }

    [Fact]
    public void Removing_the_clip_later_is_a_conflict()
    {
        var s = Session();
        var trim = s.Execute(new SetClipRangeCommand(2, 123.14, 190.12))!;
        s.Remove(2);
        Assert.Throws<EditException>(() => s.Revert(trim));
    }

    [Fact]
    public void Undone_or_already_reverted_edits_cannot_be_reverted()
    {
        var s = Session();
        var trim = s.Execute(new SetClipRangeCommand(2, 123.14, 190.12))!;
        s.Revert(trim);
        Assert.Throws<EditException>(() => s.Revert(trim));

        var other = s.Execute(new RenameClipCommand(1, "Cold open"))!;
        s.Undo();
        Assert.Throws<EditException>(() => s.Revert(other));
    }

    [Fact]
    public void The_command_works_on_projects_alone()
    {
        var before = Sample.Project;
        var after = new SetClipIncludedCommand(6, true).Apply(before);
        var command = new RevertEditCommand(before, after, "Kept clip 5");

        Assert.Equal(before.Clips, command.Apply(after).Clips);
        Assert.Throws<EditException>(() => command.Apply(before));
    }
}
