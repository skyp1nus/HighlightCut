using HighlightCut.Core.Editing;
using HighlightCut.Core.Editing.Commands;
using HighlightCut.Core.Model;

namespace HighlightCut.Core.Tests.Editing;

public class EditorSessionTests
{
    private static EditorSession Open()
    {
        var s = new EditorSession();
        s.Load(Sample.Project);
        return s;
    }

    [Fact]
    public void Edits_are_recorded_and_undo_redo_restore_each_state()
    {
        var s = Open();
        s.SetIncluded(3, false);
        s.Move(1, 3);
        Assert.Equal(2, s.History.Position);

        Assert.True(s.Undo());
        Assert.True(s.Project.Get(3) is { IsIncluded: false });
        Assert.Equal(0, s.Project.IndexOf(1));
        Assert.True(s.Undo());
        Assert.Same(Sample.Project, s.Project);
        Assert.False(s.Undo());

        Assert.True(s.Redo());
        Assert.True(s.Redo());
        Assert.Equal(3, s.Project.IndexOf(1));
        Assert.False(s.Redo());
    }

    [Fact]
    public void A_new_edit_after_undo_discards_the_redo_branch()
    {
        var s = Open();
        s.Rename(1, "Opening");
        s.Undo();
        s.Rename(2, "Setup 2");
        Assert.False(s.History.CanRedo);
        Assert.Single(s.History.Entries);
    }

    [Fact]
    public void Edits_that_change_nothing_are_not_recorded()
    {
        var s = Open();
        Assert.Null(s.Execute(new SetClipIncludedCommand(1, true)));
        Assert.Empty(s.History.Entries);
    }

    [Fact]
    public void Invalid_edits_throw_and_leave_the_project_unchanged()
    {
        var s = Open();
        Assert.Throws<EditException>(() => s.Remove(99));
        Assert.Same(Sample.Project, s.Project);
        Assert.Empty(s.History.Entries);
    }

    [Fact]
    public void Edits_with_the_same_merge_key_undo_as_one_step()
    {
        var s = Open();
        s.Trim(1, ClipEdge.In, 11, mergeKey: "drag-1");
        s.Trim(1, ClipEdge.In, 10, mergeKey: "drag-1");
        s.Trim(1, ClipEdge.In, 9, mergeKey: "drag-1");
        Assert.Single(s.History.Entries);
        Assert.Equal(9, s.Project.Get(1).Start);

        s.Undo();
        Assert.Equal(12.04, s.Project.Get(1).Start);
        s.Redo();
        Assert.Equal(9, s.Project.Get(1).Start);

        s.Trim(1, ClipEdge.In, 8, mergeKey: "drag-2");
        Assert.Equal(2, s.History.Entries.Count);
    }

    [Fact]
    public void Trim_clamps_to_the_source_and_the_minimum_length()
    {
        var s = Open();
        Assert.Equal(0, s.Trim(1, ClipEdge.In, -5));
        Assert.Equal(45.32 - EditRules.MinClipDuration, s.Trim(1, ClipEdge.In, 99), 9);
        Assert.Equal(872.48, s.Trim(5, ClipEdge.Out, 2000));
        Assert.Equal(750 + EditRules.MinClipDuration, s.Trim(5, ClipEdge.Out, 10), 9);
    }

    [Fact]
    public void Trim_snaps_to_the_nearest_keyframe_within_the_threshold()
    {
        var s = Open();
        s.Keyframes = [10.0, 12.5, 14.0];
        Assert.Equal(12.5, s.Trim(1, ClipEdge.In, 12.3, snapThreshold: 0.5));
        Assert.Equal(11.2, s.Trim(1, ClipEdge.In, 11.2, snapThreshold: 0.5));
    }

    [Fact]
    public void Keep_range_inserts_the_clip_by_source_position()
    {
        var s = Open();
        var clip = s.KeepRange(45.32, 118.6);
        Assert.Equal(1, s.Project.IndexOf(clip.Id));
        Assert.Equal("Clip 7", clip.Label);
        var last = s.KeepRange(828.72, 872.48);
        Assert.Equal(s.Project.Clips.Count - 1, s.Project.IndexOf(last.Id));
    }

    [Fact]
    public void Split_returns_the_second_part()
    {
        var s = Open();
        var second = s.Split(3, 300);
        Assert.Equal(300, second.Start);
        Assert.Equal(3, s.Project.IndexOf(second.Id));
    }

    [Fact]
    public void Changed_reports_kind_entry_and_both_projects()
    {
        var s = Open();
        var events = new List<ProjectChangedEventArgs>();
        s.Changed += (_, e) => events.Add(e);

        s.SetIncluded(1, false);
        s.Undo();
        s.Redo();
        s.Load(Project.Empty);

        Assert.Equal([ProjectChangeKind.Edited, ProjectChangeKind.Undone, ProjectChangeKind.Redone, ProjectChangeKind.Loaded],
            events.Select(e => e.Kind));
        Assert.Equal("Excluded clip 1", events[0].Entry!.Description);
        Assert.Same(Sample.Project, events[0].Previous);
        Assert.Null(events[3].Entry);
        Assert.Empty(s.History.Entries);
    }

    [Fact]
    public void Entries_record_origin_output_delta_and_changed_clips()
    {
        var s = Open();
        var entry = s.Execute(new SetClipRangeCommand(2, 123.14, 190.12), EditOrigin.Assistant)!;
        Assert.Equal(EditOrigin.Assistant, entry.Origin);
        Assert.Equal(-4.54, entry.OutputDelta, 6);
        Assert.Equal([2], entry.ChangedClipIds);
        Assert.Equal("trim_segment", entry.Command.Name);
    }

    [Fact]
    public void Undo_through_and_redo_through_move_to_an_entry()
    {
        var s = Open();
        var first = s.Execute(new RenameClipCommand(1, "a"))!;
        var second = s.Execute(new RenameClipCommand(2, "b"))!;
        s.Execute(new RenameClipCommand(3, "c"));

        s.UndoThrough(second);
        Assert.Equal(1, s.History.Position);
        Assert.True(s.History.IsApplied(first));
        s.RedoThrough(second);
        Assert.Equal(2, s.History.Position);
    }

    [Fact]
    public void History_drops_the_oldest_entries_beyond_its_capacity()
    {
        var s = new EditorSession(Sample.Project, historyCapacity: 2);
        s.Rename(1, "a");
        s.Rename(2, "b");
        s.Rename(3, "c");

        Assert.Equal(["Renamed clip 2 to “b”", "Renamed clip 3 to “c”"], s.History.Entries.Select(e => e.Description));
        Assert.True(s.Undo());
        Assert.True(s.Undo());
        Assert.False(s.Undo());
        Assert.Equal("a", s.Project.Get(1).Label);
        Assert.Throws<ArgumentOutOfRangeException>(() => new History(0));
    }
}

public class HistoryIdTests
{
    [Fact]
    public void Entries_get_increasing_ids_that_survive_undo_and_merging()
    {
        var session = new EditorSession(Sample.Project);
        var a = session.Execute(new RenameClipCommand(1, "A"))!;
        var b = session.Execute(new SetClipRangeCommand(2, 118, 190), mergeKey: "drag")!;
        var b2 = session.Execute(new SetClipRangeCommand(2, 117, 190), mergeKey: "drag")!;
        session.Undo();
        var c = session.Execute(new RenameClipCommand(1, "C"))!;

        Assert.Equal(1, a.Id);
        Assert.Equal(2, b.Id);
        Assert.Equal(b.Id, b2.Id);
        Assert.Equal(3, c.Id);
        Assert.Same(a, session.History.Find(1));
        Assert.Null(session.History.Find(2));
    }

    [Fact]
    public void Track_volume_is_not_an_edit_and_undo_keeps_it()
    {
        var s = new EditorSession(Sample.Project);
        var kinds = new List<ProjectChangeKind>();
        s.Changed += (_, e) => kinds.Add(e.Kind);
        s.SetIncluded(3, false);

        Assert.True(s.SetTrackMix(new TrackMix(2, -6)));
        Assert.False(s.SetTrackMix(new TrackMix(2, -6)));
        Assert.Equal(1, s.History.Position);
        Assert.Equal(ProjectChangeKind.Mixed, kinds[^1]);

        Assert.True(s.Undo());
        Assert.True(s.Project.Get(3).IsIncluded);
        Assert.Equal(-6, s.Project.MixOf(2).GainDb);
        Assert.True(s.Redo());
        Assert.Equal(-6, s.Project.MixOf(2).GainDb);
    }

    [Fact]
    public void Track_volume_is_clamped_and_a_default_mix_is_dropped()
    {
        var s = new EditorSession(Sample.Project);
        s.SetTrackMix(new TrackMix(1, 30.04, IsMuted: true));
        Assert.Equal(new TrackMix(1, TrackMix.MaxGainDb, true), Assert.Single(s.Project.AudioMix));

        s.SetTrackMix(new TrackMix(1));
        Assert.Empty(s.Project.AudioMix);
    }
}
