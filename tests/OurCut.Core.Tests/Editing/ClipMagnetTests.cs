using OurCut.Core.Editing;
using OurCut.Core.Editing.Commands;
using OurCut.Core.Model;

namespace OurCut.Core.Tests.Editing;

/// <summary>Clips snapping onto their neighbours, never sharing source time, and joining.</summary>
public class ClipMagnetTests
{
    private static EditorSession Open(Project? project = null)
    {
        var s = new EditorSession();
        s.Load(project ?? Sample.Project);
        return s;
    }

    /// <summary>A project saved before clips were kept apart: clip 2 starts inside clip 1.</summary>
    private static Project Overlapping(params Clip[] clips) => new("old", Sample.Source, [.. clips]);

    // ---- Magnet --------------------------------------------------------------------------

    [Fact]
    public void A_trimmed_end_snaps_onto_the_neighbours_edge_within_the_threshold()
    {
        var s = Open();
        Assert.Equal(45.32, s.Trim(2, ClipEdge.In, 45.7, snapThreshold: 0.5));
        Assert.Equal(s.Project.Get(1).End, s.Project.Get(2).Start);

        s = Open();
        Assert.Equal(118.6, s.Trim(1, ClipEdge.Out, 118.2, snapThreshold: 0.5));
        // Outside the threshold it lets go.
        Assert.Equal(118.0, s.Trim(1, ClipEdge.Out, 118.0, snapThreshold: 0.1));
        Assert.Empty(s.Project.Overlaps());
    }

    [Fact]
    public void The_neighbours_edge_wins_over_a_nearer_keyframe()
    {
        var s = Open();
        s.Keyframes = [45.5];
        Assert.Equal(45.32, s.Trim(2, ClipEdge.In, 45.6, snapThreshold: 0.5));
    }

    [Fact]
    public void The_magnet_works_without_keyframe_snapping_and_not_at_all_with_a_zero_threshold()
    {
        var s = Open();
        s.Keyframes = [60.0];
        Assert.Equal(45.32, s.Trim(2, ClipEdge.In, 45.6, snapThreshold: 0.5, snapToKeyframes: false));
        Assert.Equal(60.2, s.Trim(2, ClipEdge.In, 60.2, snapThreshold: 0.5, snapToKeyframes: false));
        // Alt: no threshold, no snapping of either kind.
        Assert.Equal(45.6, s.Trim(2, ClipEdge.In, 45.6, snapThreshold: 0));
        Assert.Equal(60.2, s.Trim(2, ClipEdge.In, 60.2, snapThreshold: 0));
    }

    [Fact]
    public void A_trim_stops_at_the_neighbours_edge()
    {
        var s = Open();
        Assert.Equal(45.32, s.Trim(2, ClipEdge.In, 20));
        Assert.Equal(242.88, s.Trim(2, ClipEdge.Out, 300));
        // The excluded clip is a neighbour too: clips never share source time.
        Assert.Equal(640.0, s.Trim(4, ClipEdge.Out, 700));
        Assert.Empty(s.Project.Overlaps());
    }

    // ---- No overlaps ---------------------------------------------------------------------

    [Fact]
    public void Adding_a_clip_over_another_is_refused_with_a_clear_message()
    {
        var s = Open();
        var e = Assert.Throws<EditException>(() => s.AddClip(40, 50));
        Assert.Contains("overlaps clip 1", e.Message);
        Assert.Same(Sample.Project, s.Project);
        // Right up to the edges is fine.
        s.AddClip(45.32, 118.6);
        Assert.Empty(s.Project.Overlaps());
    }

    [Fact]
    public void Setting_a_range_into_a_neighbour_is_refused()
    {
        var s = Open();
        var e = Assert.Throws<EditException>(() => s.SetRange(2, 40, 190.12));
        Assert.Contains("overlaps clip 1", e.Message);
        Assert.Throws<EditException>(() => s.Execute(new SetClipRangeCommand(3, 242.88, 500)));
        Assert.Same(Sample.Project, s.Project);
    }

    [Fact]
    public void A_batch_that_would_overlap_is_refused_as_a_whole()
    {
        var s = Open();
        var batch = new BatchCommand("edit_timeline", "Two edits", [new RenameClipCommand(1, "Opening"), new AddClipCommand(100, 130)]);
        Assert.Throws<EditException>(() => s.Execute(batch));
        Assert.Same(Sample.Project, s.Project);
    }

    [Fact]
    public void Reverting_an_edit_whose_time_a_later_edit_took_is_refused()
    {
        var s = Open();
        var shorten = s.Execute(new SetClipRangeCommand(1, 12.04, 40))!;
        s.Trim(2, ClipEdge.In, 42);
        var e = Assert.Throws<EditException>(() => s.Revert(shorten));
        Assert.Contains("overlaps", e.Message);
        Assert.Equal(40, s.Project.Get(1).End);
    }

    [Fact]
    public void Keep_range_takes_the_free_part_and_refuses_a_covered_range()
    {
        var s = Open();
        var clip = s.KeepRange(40, 60, "Kept");
        Assert.Equal((45.32, 60.0), (clip.Start, clip.End));
        Assert.Equal(1, s.Project.IndexOf(clip.Id));
        var e = Assert.Throws<EditException>(() => s.KeepRange(20, 30));
        Assert.Equal("That is already in clip 1.", e.Message);
        Assert.Empty(s.Project.Overlaps());
    }

    [Fact]
    public void Free_range_starts_after_a_covering_clip_and_stops_at_the_next()
    {
        var p = Sample.Project;
        Assert.Equal(new TimeRange(45.32, 100), p.FreeRange(30, 100));
        Assert.Equal(new TimeRange(45.32, 118.6), p.FreeRange(30, 300));
        Assert.Equal(new TimeRange(0, 12.04), p.FreeRange(0, 20));
        Assert.Null(p.FreeRange(13, 40));
    }

    [Fact]
    public void Split_and_cut_never_create_overlaps()
    {
        var s = Open();
        s.Split(3, 300);
        s.Execute(new CutRangesCommand([new TimeRange(20, 25), new TimeRange(150, 160)]));
        Assert.Empty(s.Project.Overlaps());
    }

    // ---- Projects saved with overlapping clips ---------------------------------------------

    [Fact]
    public void An_old_project_with_overlaps_still_opens_and_edits_may_shrink_them_but_not_grow_them()
    {
        var s = Open(Overlapping(new Clip(1, "A", 10, 20), new Clip(2, "B", 15, 30), new Clip(3, "C", 40, 50)));
        Assert.Single(s.Project.Overlaps());

        s.SetRange(1, 10, 18);
        Assert.Throws<EditException>(() => s.SetRange(1, 10, 22));
        // Its in-point cannot move back into clip 1, only forward.
        Assert.Equal(15, s.Trim(2, ClipEdge.In, 5));
        Assert.Equal(17, s.Trim(2, ClipEdge.In, 17));
        // Unrelated edits and splits still work.
        s.Rename(3, "Outro");
        s.Split(1, 12);
        Assert.Equal(1, s.Project.OverlapDuration(), 9);
    }

    [Fact]
    public void Output_parts_leave_out_seconds_an_earlier_clip_already_exports()
    {
        var p = Overlapping(new Clip(1, "A", 10, 20), new Clip(2, "B", 15, 30), new Clip(3, "Inside", 12, 14),
            new Clip(4, "Around", 5, 40), new Clip(5, "Off", 0, 50, IsIncluded: false));

        var parts = p.OutputParts().Select(x => (x.Clip.Id, x.Start, x.End)).ToList();

        Assert.Equal([(1, 10.0, 20.0), (2, 20.0, 30.0), (4, 5.0, 10.0), (4, 30.0, 40.0)], parts);
        Assert.Equal(35, p.OutputDuration, 9);
        // Without overlaps every included clip is exported whole.
        Assert.Equal(Sample.Project.IncludedClips.Select(c => (c.Id, c.Start, c.End)),
            Sample.Project.OutputParts().Select(x => (x.Clip.Id, x.Start, x.End)));
    }

    // ---- Join ----------------------------------------------------------------------------

    [Fact]
    public void Touching_clips_join_into_one_that_keeps_the_first_clips_name_in_one_undo_step()
    {
        var s = Open();
        s.Trim(2, ClipEdge.In, 45.32);
        int before = s.History.Entries.Count;

        var joined = s.JoinWithNext(1);

        Assert.Equal((1, "Intro", 12.04, 190.12), (joined.Id, joined.Label, joined.Start, joined.End));
        Assert.Null(s.Project.Find(2));
        Assert.Equal(0, s.Project.IndexOf(1));
        Assert.Equal(before + 1, s.History.Entries.Count);
        Assert.Equal("Joined clips 1 and 2", s.History.NextUndo!.Description);

        s.Undo();
        Assert.Equal((12.04, 45.32), (s.Project.Get(1).Start, s.Project.Get(1).End));
        Assert.Equal((45.32, 190.12), (s.Project.Get(2).Start, s.Project.Get(2).End));
        s.Redo();
        Assert.Equal(190.12, s.Project.Get(1).End);
    }

    [Fact]
    public void A_gap_under_half_a_second_is_closed_by_the_join_and_a_longer_one_is_refused()
    {
        var s = Open();
        s.SetRange(2, 45.7, 190.12);
        Assert.True(s.CanJoinWithNext(1));
        Assert.Equal(190.12, s.JoinWithNext(1).End);

        Assert.False(s.CanJoinWithNext(1));
        var e = Assert.Throws<EditException>(() => s.JoinWithNext(1));
        Assert.Contains("apart", e.Message);
    }

    [Fact]
    public void The_joined_clip_keeps_the_first_clips_settings()
    {
        var s = Open();
        s.Trim(2, ClipEdge.In, 45.32);
        s.SetIncluded(1, false);
        var joined = s.JoinWithNext(1);
        Assert.False(joined.IsIncluded);
        Assert.Equal("Intro", joined.Label);
    }

    [Fact]
    public void Clips_apart_in_the_output_are_not_joined()
    {
        var s = Open();
        s.Trim(2, ClipEdge.In, 45.32);
        s.Move(2, 3);
        var e = Assert.Throws<EditException>(() => s.JoinWithNext(1));
        Assert.Contains("not next to each other", e.Message);
        Assert.Throws<EditException>(() => s.JoinWithNext(5));
    }

    [Fact]
    public void Joining_overlapping_clips_of_an_old_project_resolves_the_overlap()
    {
        var s = Open(Overlapping(new Clip(1, "A", 10, 20), new Clip(2, "B", 15, 30)));
        var joined = s.JoinWithNext(1);
        Assert.Equal((10.0, 30.0), (joined.Start, joined.End));
        Assert.Empty(s.Project.Overlaps());
    }
}
