using HighlightCut.Core.Editing;
using HighlightCut.Core.Editing.Commands;
using HighlightCut.Core.Model;

namespace HighlightCut.Core.Tests.Editing;

/// <summary>The clip commands and session helpers in projects with two and three videos (see <see cref="Videos"/>).</summary>
public class SeveralVideosTests
{
    // ---- Adding clips ------------------------------------------------------------------------

    [Fact]
    public void A_clip_names_its_video_and_is_checked_against_it()
    {
        var p = new AddClipCommand(30, 50, SourceId: 2).Apply(Videos.Two);
        Assert.Equal(new Clip(3, "Clip 3", 30, 50, Color: p.Get(3).Color, SourceId: 2), p.Get(3));

        var past = Assert.Throws<EditException>(() => new AddClipCommand(40, 60, SourceId: 2).Apply(Videos.Two));
        Assert.Equal("Out-point 60 s is after the end of video 2 (50 s).", past.Message);
        Assert.Equal("Video 9 is not in the project.", Assert.Throws<EditException>(() => new AddClipCommand(1, 2, SourceId: 9).Apply(Videos.Two)).Message);
        Assert.Equal("The project has several videos; say which one the clip is in.",
            Assert.Throws<EditException>(() => new AddClipCommand(30, 40).Apply(Videos.Two)).Message);
    }

    [Fact]
    public void An_overlap_is_refused_only_within_one_video_and_named_in_timeline_times()
    {
        var e = Assert.Throws<EditException>(() => new AddClipCommand(15, 30, SourceId: 2).Apply(Videos.Two));
        Assert.StartsWith("01:55.000 – 02:10.000 overlaps clip 2 (01:50.000 – 02:00.000).", e.Message, StringComparison.Ordinal);
        // The same seconds in video a are free of clip 2.
        Assert.NotNull(new AddClipCommand(25, 30, SourceId: 1).Apply(Videos.Two).Find(3));
    }

    [Fact]
    public void Adding_a_timeline_range_over_a_join_makes_one_clip_per_video_in_one_step()
    {
        var s = Videos.Open(Videos.Three);
        var first = s.AddClip(90, 160, "Across");

        Assert.Equal(["Across", "Across · 2", "Across · 3"], s.Project.Clips.Select(c => c.Label));
        Assert.Equal([(1, 90.0, 100.0), (2, 0.0, 50.0), (3, 0.0, 10.0)], s.Project.Clips.Select(c => (c.SourceId, c.Start, c.End)));
        Assert.Equal([new TimeRange(90, 100), new TimeRange(100, 150), new TimeRange(150, 160)], s.Project.Clips.Select(s.Project.TimelineRange));
        Assert.Equal(1, first.Id);
        var entry = Assert.Single(s.History.Entries);
        Assert.Equal("Added 3 clips, one per video", entry.Description);

        s.Undo();
        Assert.Empty(s.Project.Clips);
    }

    [Fact]
    public void A_sliver_past_a_join_is_left_out_and_a_range_ending_on_one_stays_in_its_video()
    {
        var s = Videos.Open(Videos.Three);
        s.AddClip(95, 100.1);
        Assert.Equal((1, 95.0, 100.0), s.Project.Clips.Select(c => (c.SourceId, c.Start, c.End)).Single());

        s.AddClip(140, 150);
        Assert.Equal((2, 40.0, 50.0), s.Project.Clips.Select(c => (c.SourceId, c.Start, c.End)).Last());
    }

    [Theory]
    [InlineData(120, 120, "Clips must be at least 0.2 s long.")]
    [InlineData(99.9, 100.1, "That range has less than 0.2 s in each video; a clip cannot cross from one video into the next.")]
    [InlineData(170, 181, "Out-point 181 s is after the end of the timeline (180 s).")]
    [InlineData(-1, 5, "In-point -1 s is before the start of the timeline.")]
    public void Timeline_ranges_that_make_no_clip_are_refused(double start, double end, string message)
    {
        var s = Videos.Open(Videos.Three);
        Assert.Equal(message, Assert.Throws<EditException>(() => s.AddClip(start, end)).Message);
        Assert.Empty(s.History.Entries);
    }

    [Fact]
    public void Keep_over_the_join_makes_one_clip_per_video_placed_by_timeline_position()
    {
        var s = Videos.Open(Videos.Two);
        var first = s.KeepRange(95, 105, "Join");

        Assert.Equal(["A intro", "Join", "Join · 2", "B intro"], s.Project.Clips.Select(c => c.Label));
        Assert.Equal(first.Id, s.Project.Clips[1].Id);
        Assert.Equal([(1, 95.0, 100.0), (2, 0.0, 5.0)], s.Project.Clips.Skip(1).Take(2).Select(c => (c.SourceId, c.Start, c.End)));
        Assert.Single(s.History.Entries);
    }

    [Fact]
    public void Keep_takes_the_free_part_up_to_the_next_clip_even_in_the_next_video()
    {
        var s = Videos.Open(Videos.Two.WithClips([new Clip(1, "End of a", 80, 100, SourceId: 1), new Clip(2, "B", 20, 30, SourceId: 2)]));
        // From inside clip 1 the free part starts at the join and stops at clip 2.
        var kept = s.KeepRange(90, 140);
        Assert.Equal((2, 0.0, 20.0), (kept.SourceId, kept.Start, kept.End));
        Assert.Equal("That is already in clip 1.", Assert.Throws<EditException>(() => s.KeepRange(85, 95)).Message);
    }

    // ---- Changing clips ----------------------------------------------------------------------

    [Fact]
    public void Trim_and_split_stay_inside_the_clips_video()
    {
        var s = Videos.Open(Videos.Two);
        // Timeline times: clip 2 is 110–120 on the timeline.
        s.SetRange(2, 105, 130);
        Assert.Equal((5.0, 30.0), (s.Project.Get(2).Start, s.Project.Get(2).End));

        var e = Assert.Throws<EditException>(() => s.SetRange(2, 95, 130));
        Assert.Contains("Clip 2 is in video 2, which runs 01:40.000 – 02:30.000 on the timeline", e.Message, StringComparison.Ordinal);
        Assert.Throws<EditException>(() => new SetClipRangeCommand(2, 5, 60).Apply(s.Project));

        var second = s.Split(2, 115);
        Assert.Equal((2, 15.0, 30.0), (second.SourceId, second.Start, second.End));
        Assert.Equal(15, s.Project.Get(2).End);
        Assert.Throws<EditException>(() => s.Split(1, 110));
    }

    [Fact]
    public void Trim_stops_at_the_ends_of_its_video_and_snaps_only_to_clips_in_it()
    {
        var s = Videos.Open(Videos.Two.WithClips([new Clip(1, "A", 10, 20, SourceId: 1), new Clip(2, "B", 1, 20, SourceId: 2)]));
        // Dragging clip 2's in-point into video a stops at the start of video b.
        Assert.Equal(100, s.Trim(2, ClipEdge.In, 60));
        Assert.Equal(0, s.Project.Get(2).Start);
        // Clip 1's out-point stops at the end of video a, and clip 2 (at 100 on the timeline) does not pull it.
        Assert.Equal(100, s.Trim(1, ClipEdge.Out, 130, snapThreshold: 1));
        Assert.Equal(100, s.Project.Get(1).End);
        Assert.Equal(97, s.Trim(1, ClipEdge.Out, 97, snapThreshold: 1));
    }

    [Fact]
    public void Trims_snap_to_keyframes_on_the_timeline()
    {
        var s = Videos.Open(Videos.Two);
        s.Keyframes = [112.5, 135];
        Assert.Equal(112.5, s.Trim(2, ClipEdge.In, 112.4, snapThreshold: 0.5));
        Assert.Equal(12.5, s.Project.Get(2).Start, 9);
    }

    [Fact]
    public void Join_refuses_clips_of_different_videos()
    {
        var p = Videos.Two.WithClips([new Clip(1, "A", 80, 100, SourceId: 1), new Clip(2, "B", 0, 20, SourceId: 2)]);
        Assert.Equal("Clips 1 and 2 are in different videos; a clip cannot run from one video into the next.",
            new JoinClipsCommand(1, 2).Problem(p));
        var e = Assert.Throws<EditException>(() => JoinClipsCommand.WithNext(p, 1));
        Assert.Equal("Clip 1 is the last clip of video 1; clips in different videos cannot be joined.", e.Message);
        Assert.False(new EditorSession(p).CanJoinWithNext(1));
    }

    [Fact]
    public void Join_with_next_looks_in_the_clips_own_video()
    {
        // Clip 2 (video b, 0–5) starts earlier on its video than clip 3 (video a, 20–30), but it is in another video.
        var p = Videos.Two.WithClips([new Clip(1, "A", 10, 20, SourceId: 1), new Clip(2, "B", 0, 5, SourceId: 2),
            new Clip(3, "A2", 20, 30, SourceId: 1)]);
        Assert.Equal(new JoinClipsCommand(1, 3), JoinClipsCommand.WithNext(p, 1));
    }

    [Fact]
    public void Cuts_apply_to_the_clips_of_their_video()
    {
        var cut = new CutRangesCommand([new SourceRange(2, 12, 14)]).Apply(Videos.Two);
        Assert.Equal(Videos.Two.Get(1), cut.Get(1));
        Assert.Equal([(2, 10.0, 12.0), (2, 14.0, 20.0)], cut.ClipsOf(2).Select(c => (c.SourceId, c.Start, c.End)));

        // A timeline range over the join cuts both videos.
        var s = Videos.Open(Videos.Two.WithClips([new Clip(1, "A", 80, 100, SourceId: 1), new Clip(2, "B", 0, 20, SourceId: 2)]));
        s.Execute(new CutRangesCommand(TimelineEdits.ToSources(s.Project, [new TimeRange(95, 105)])));
        Assert.Equal([(1, 80.0, 95.0), (2, 5.0, 20.0)], s.Project.Clips.Select(c => (c.SourceId, c.Start, c.End)));
    }

    [Fact]
    public void New_clips_take_colours_apart_from_their_neighbours_in_their_video()
    {
        var p = new AddClipCommand(20, 30, Index: 2, SourceId: 2).Apply(Videos.Two);
        Assert.NotEqual(p.Get(2).Color, p.Get(3).Color);
    }

    // ---- Videos ------------------------------------------------------------------------------

    [Fact]
    public void Adding_a_video_appends_it_with_a_new_id_and_undo_takes_it_back()
    {
        var s = Videos.Open(Videos.Two);
        var c = s.AddSource(Videos.C with { Id = 1 });
        Assert.Equal(3, c.Id);
        Assert.Equal([1, 2, 3], s.Project.Sources.Select(x => x.Id));
        Assert.Equal(180, s.Project.TimelineDuration);
        Assert.Equal("Added video 3 (c.mp4)", s.History.Entries[^1].Description);
        Assert.Equal("add_video", s.History.Entries[^1].Command.Name);

        s.Undo();
        Assert.Same(Videos.Two, s.Project);
        s.Redo();
        Assert.Equal(3, s.Project.Sources.Count);
    }

    [Fact]
    public void A_video_without_length_is_refused()
    {
        var e = Assert.Throws<EditException>(() => new AddSourceCommand(new SourceMedia("/v/empty.mp4", 0, 30)).Apply(Videos.Two));
        Assert.Equal("empty.mp4 has no length, so it cannot go on the timeline.", e.Message);
        Assert.Throws<EditException>(() => new AddSourceCommand(Videos.C, Id: 2).Apply(Videos.Two));
    }

    [Fact]
    public void Removing_a_video_removes_its_clips_in_one_undo_step()
    {
        var s = Videos.Open(Videos.Two);
        s.RemoveSource(1);
        Assert.Equal([2], s.Project.Sources.Select(x => x.Id));
        Assert.Equal([2], s.Project.Clips.Select(c => c.Id));
        Assert.Equal(new TimeRange(10, 20), s.Project.TimelineRange(s.Project.Get(2)));
        Assert.Equal("Removed video 1 (a.mp4) and its 1 clip", s.History.Entries[^1].Description);

        s.Undo();
        Assert.Same(Videos.Two, s.Project);
    }

    [Fact]
    public void A_removed_videos_id_is_not_handed_out_again()
    {
        var s = Videos.Open(Videos.Three);
        s.RemoveSource(3);
        Assert.Equal(4, s.AddSource(Videos.C).Id);
    }

    [Fact]
    public void The_only_video_and_unknown_videos_cannot_be_removed()
    {
        var one = new Project("one", Videos.A, []);
        Assert.Equal("a.mp4 is the project's only video; add another one before removing it.",
            Assert.Throws<EditException>(() => new RemoveSourceCommand(1).Apply(one)).Message);
        Assert.Equal("Video 7 is not in the project.", Assert.Throws<EditException>(() => new RemoveSourceCommand(7).Apply(Videos.Two)).Message);
    }

    [Fact]
    public void Removing_the_video_the_selected_clip_is_in_leaves_nothing_to_edit_until_undone()
    {
        var s = Videos.Open(Videos.Two);
        int selected = 2;
        s.RemoveSource(2);

        Assert.Null(s.Project.Find(selected));
        Assert.Equal("Clip 2 does not exist.", Assert.Throws<EditException>(() => s.Split(selected, 115)).Message);
        Assert.Throws<EditException>(() => s.Trim(selected, ClipEdge.Out, 118));
        s.Undo();
        Assert.Equal(15, s.Split(selected, 115).Start);
    }

    [Fact]
    public void Moving_a_video_moves_its_clips_on_the_timeline_but_not_in_the_output()
    {
        var s = Videos.Open(Videos.Three.WithClips([new Clip(1, "A", 10, 20, SourceId: 1), new Clip(2, "C", 0, 10, SourceId: 3)]));
        s.MoveSource(3, 0);

        Assert.Equal([3, 1, 2], s.Project.Sources.Select(x => x.Id));
        Assert.Equal([new TimeRange(40, 50), new TimeRange(0, 10)], s.Project.Clips.Select(s.Project.TimelineRange));
        Assert.Equal([1, 2], s.Project.Clips.Select(c => c.Id));
        Assert.Equal("Moved video 3 to position 1", s.History.Entries[^1].Description);
        Assert.Null(s.Execute(new MoveSourceCommand(3, 0)));
        Assert.Equal("Position 4 is outside the video list (1–3).", Assert.Throws<EditException>(() => s.MoveSource(1, 3)).Message);

        s.Undo();
        Assert.Equal([1, 2, 3], s.Project.Sources.Select(x => x.Id));
    }

    [Fact]
    public void A_batch_can_add_a_video_and_clips_in_it()
    {
        var s = Videos.Open(Videos.Two);
        s.Execute(new BatchCommand("add_video", "Added c.mp4 with a clip",
            [new AddSourceCommand(Videos.C), new AddClipCommand(0, 30, "All of c", SourceId: 3)]));
        Assert.Equal(new TimeRange(150, 180), s.Project.TimelineRange(s.Project.Get(3)));
        s.Undo();
        Assert.Same(Videos.Two, s.Project);
    }

    [Fact]
    public void No_edit_may_leave_a_clip_without_its_video()
    {
        var s = Videos.Open(Videos.Two);
        var e = Assert.Throws<EditException>(() => s.Execute(new DropVideoCommand(2)));
        Assert.Equal("Clip 2 would be left without its video.", e.Message);
        Assert.Same(Videos.Two, s.Project);
    }

    /// <summary>Takes a video away and nothing else, as a faulty command would.</summary>
    private sealed record DropVideoCommand(int SourceId) : IEditCommand
    {
        public string Name => "drop";

        public string Describe(Project before) => "Dropped a video";

        public Project Apply(Project project) => project with { Sources = project.Sources.RemoveAll(x => x.Id == SourceId) };
    }

    // ---- Reverting ---------------------------------------------------------------------------

    [Fact]
    public void Reverting_a_removed_video_puts_it_and_its_clips_back_and_keeps_later_edits()
    {
        var s = Videos.Open(Videos.Three.WithClips([new Clip(1, "A", 10, 20, SourceId: 1), new Clip(2, "B", 0, 10, SourceId: 2)]));
        var remove = s.Execute(new RemoveSourceCommand(2))!;
        s.Rename(1, "Opening");

        s.Revert(remove);
        Assert.Equal([1, 2, 3], s.Project.Sources.Select(x => x.Id));
        Assert.Equal(["Opening", "B"], s.Project.Clips.Select(c => c.Label));
        Assert.Equal(new TimeRange(100, 110), s.Project.TimelineRange(s.Project.Get(2)));
    }

    [Fact]
    public void Reverting_an_added_video_removes_it_unless_clips_were_cut_from_it_since()
    {
        var s = Videos.Open(Videos.Two);
        var add = s.Execute(new AddSourceCommand(Videos.C))!;
        s.Rename(1, "Opening");
        s.Revert(add);
        Assert.Equal([1, 2], s.Project.Sources.Select(x => x.Id));
        Assert.Equal("Opening", s.Project.Get(1).Label);

        s.Undo();
        s.AddClip(160, 170);
        var e = Assert.Throws<EditException>(() => s.Revert(add));
        Assert.Equal("Clip 3 was cut from c.mp4 after “Added video 3 (c.mp4)”, so the video cannot go. Remove the clip first.", e.Message);
    }

    [Fact]
    public void Reverting_a_move_restores_the_order_unless_the_videos_were_moved_again()
    {
        var s = Videos.Open(Videos.Three);
        var move = s.Execute(new MoveSourceCommand(1, 2))!;
        s.Revert(move);
        Assert.Equal([1, 2, 3], s.Project.Sources.Select(x => x.Id));

        s.Undo();
        s.MoveSource(2, 2);
        Assert.Equal("Videos were reordered after “Moved video 1 to position 3”. Undo that first.",
            Assert.Throws<EditException>(() => s.Revert(move)).Message);
    }

    [Fact]
    public void Reverting_a_keep_over_the_join_removes_both_clips()
    {
        var s = Videos.Open(Videos.Two);
        s.KeepRange(95, 105);
        s.Rename(1, "Opening");
        s.Revert(s.History.Entries[0]);
        Assert.Equal(["Opening", "B intro"], s.Project.Clips.Select(c => c.Label));
    }
}
