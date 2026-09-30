using HighlightCut.Core.Editing;
using HighlightCut.Core.Model;

namespace HighlightCut.Core.Tests.Model;

/// <summary>The videos end to end on one timeline, and the rules that work within one video.</summary>
public class TimelineTests
{
    private static readonly Project Three = Videos.Three;

    [Fact]
    public void Videos_sit_end_to_end()
    {
        Assert.Equal(180, Three.TimelineDuration);
        Assert.Equal([0, 100, 150], Three.Sources.Select(s => Three.OffsetOf(s.Id)));
        Assert.Equal(new TimeRange(100, 150), Three.SpanOf(2));
        Assert.Equal(125, Three.ToTimeline(2, 25));
        Assert.Equal(160, Three.ToTimeline(new SourceTime(3, 10)));
        Assert.Throws<EditException>(() => Three.OffsetOf(9));
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(99.5, 1, 99.5)]
    [InlineData(100, 2, 0)] // A join belongs to the video that starts there.
    [InlineData(149.999, 2, 49.999)]
    [InlineData(150, 3, 0)]
    [InlineData(180, 3, 30)] // The end of the timeline is the last video's end.
    public void A_timeline_time_maps_to_one_video_and_back(double time, int source, double onVideo)
    {
        var at = Three.ToSource(time);
        Assert.Equal(source, at!.Value.SourceId);
        Assert.Equal(onVideo, at.Value.Time, 9);
        Assert.Equal(time, Three.ToTimeline(at.Value), 9);
        Assert.Equal(source, Three.SourceAt(time)!.Id);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(180.5)]
    [InlineData(double.NaN)]
    public void Times_off_the_timeline_have_no_video(double time)
    {
        Assert.Null(Three.ToSource(time));
        Assert.Null(Three.SourceAt(time));
        Assert.Null(Project.Empty.ToSource(0));
    }

    [Fact]
    public void A_clip_is_placed_by_its_video()
    {
        var p = Videos.Two;
        Assert.Equal(new TimeRange(10, 20), p.TimelineRange(p.Get(1)));
        Assert.Equal(new TimeRange(110, 120), p.TimelineRange(p.Get(2)));
        Assert.Equal(2, p.ClipAt(115)!.Id);
        Assert.Null(p.ClipAt(25));
    }

    [Fact]
    public void Reordering_the_videos_moves_their_clips_along()
    {
        var p = Videos.Two with { Sources = [Videos.B, Videos.A] };
        Assert.Equal(new TimeRange(60, 70), p.TimelineRange(p.Get(1)));
        Assert.Equal(new TimeRange(10, 20), p.TimelineRange(p.Get(2)));
        Assert.Equal(Videos.Two.Clips, p.Clips);
    }

    [Fact]
    public void A_range_is_split_at_the_joins()
    {
        Assert.Equal([new SourceRange(1, 20, 30)], Three.SplitAtSources(20, 30));
        Assert.Equal([new SourceRange(1, 90, 100), new SourceRange(2, 0, 10)], Three.SplitAtSources(90, 110));
        Assert.Equal([new SourceRange(1, 95, 100), new SourceRange(2, 0, 50), new SourceRange(3, 0, 5)], Three.SplitAtSources(95, 155));
        // Ending or starting exactly on a join leaves no empty part in the other video.
        Assert.Equal([new SourceRange(1, 90, 100)], Three.SplitAtSources(90, 100));
        Assert.Equal([new SourceRange(2, 0, 10)], Three.SplitAtSources(100, 110));
        // Nothing of no length, and nothing off the timeline.
        Assert.Empty(Three.SplitAtSources(120, 120));
        Assert.Empty(Three.SplitAtSources(200, 210));
        Assert.Equal([new SourceRange(3, 20, 30)], Three.SplitAtSources(170, 250));
    }

    [Fact]
    public void A_project_without_videos_keeps_its_clips_where_they_are()
    {
        var p = new Project("none", (SourceMedia?)null, [new Clip(1, "a", 5, 8)]);
        Assert.Equal(0, p.TimelineDuration);
        Assert.Equal(new TimeRange(5, 8), p.TimelineRange(p.Get(1)));
        Assert.Equal([new SourceRange(SourceMedia.FirstId, 2, 4)], p.SplitAtSources(2, 4));
    }

    [Fact]
    public void Clips_of_different_videos_may_cover_the_same_seconds()
    {
        var p = Videos.Two;
        Assert.Empty(p.Overlaps());
        Assert.Equal(20, p.OutputDuration);
        Assert.Equal([1, 2], p.OutputParts().Select(x => x.Clip.Id));
        Assert.Null(p.FirstOverlapping(2, 0, 5));
        Assert.Equal(2, p.FirstOverlapping(2, 15, 25)!.Id);
    }

    [Fact]
    public void Overlaps_and_output_parts_are_found_within_one_video()
    {
        var p = Videos.Two.WithClips(Videos.Two.Clips.Add(new Clip(3, "B again", 15, 30, SourceId: 2)));
        var (first, second) = Assert.Single(p.Overlaps());
        Assert.Equal((2, 3), (first.Id, second.Id));
        Assert.Equal(5, p.OverlapDuration());
        Assert.Equal([(1, 10.0, 20.0), (2, 10.0, 20.0), (3, 20.0, 30.0)], p.OutputParts().Select(x => (x.Clip.Id, x.Start, x.End)));
    }

    [Fact]
    public void Free_ranges_and_trim_limits_stay_in_one_video()
    {
        var p = Videos.Two;
        Assert.Equal(new TimeRange(20, 50), p.FreeRange(1, 15, 50));
        Assert.Equal(new TimeRange(0, 10), p.FreeRange(2, 0, 30));
        Assert.Null(p.FreeRange(2, 12, 18));
        // Clip 1 may grow to the end of video a, not into video b; clip 2 back to the start of video b.
        Assert.Equal(100, p.TrimLimit(p.Get(1), ClipEdge.Out));
        Assert.Equal(0, p.TrimLimit(p.Get(2), ClipEdge.In));
        Assert.Equal(50, p.TrimLimit(p.Get(2), ClipEdge.Out));
    }

    [Fact]
    public void The_free_timeline_range_runs_across_a_join()
    {
        var p = Videos.Two.WithClips([new Clip(1, "End of a", 80, 100, SourceId: 1), new Clip(2, "B", 20, 30, SourceId: 2)]);
        Assert.Equal(new TimeRange(100, 120), p.FreeTimelineRange(90, 140));
        Assert.Equal(new TimeRange(60, 80), p.FreeTimelineRange(60, 140));
        Assert.Null(p.FreeTimelineRange(85, 95));
        Assert.Equal(1, p.FirstOverlappingOnTimeline(85, 95)!.Id);
    }

    [Fact]
    public void Gaps_are_per_video_on_the_timeline()
    {
        Assert.Equal([new TimeRange(0, 10), new TimeRange(20, 100), new TimeRange(100, 110), new TimeRange(120, 150)],
            Videos.Two.UncoveredRanges());
        Assert.Equal(130, Videos.Two.ExcludedDuration);
    }

    [Fact]
    public void The_mix_is_kept_per_video()
    {
        var p = Videos.Two.WithMix(new TrackMix(1, -6, SourceId: 2)).WithMix(new TrackMix(1, IsMuted: true, SourceId: 1));
        Assert.Equal(-6, p.MixOf(2, 1).GainDb);
        Assert.False(p.MixOf(2, 1).IsMuted);
        Assert.True(p.MixOf(1, 1).IsMuted);
        Assert.Equal(0, p.MixOf(1, 1).GainDb);
        Assert.Equal([1, 2], p.AudioMix.Select(m => m.SourceId));
        Assert.Same(p, p.WithMix(new TrackMix(1, -6, SourceId: 2)));
        Assert.Single(p.WithMix(new TrackMix(1, SourceId: 1)).AudioMix);
    }

    [Fact]
    public void Video_ids_only_go_up()
    {
        Assert.Equal(4, Three.NextSourceId);
        var withoutC = Three.WithSources(Three.Sources.RemoveAt(2));
        Assert.Equal(4, withoutC.NextSourceId);
        Assert.Equal(2, new Project("one", Videos.A, []).NextSourceId);
        Assert.Equal(2, Three.SourceNumberOf(2));
        Assert.Same(Videos.A, Three.Source);
    }

    [Fact]
    public void A_video_probed_again_replaces_the_one_with_its_id()
    {
        var probed = Videos.B with { Duration = 51 };
        var p = Three.WithSource(probed);
        Assert.Equal(probed, p.GetSource(2));
        Assert.Equal(181, p.TimelineDuration);
        Assert.Throws<EditException>(() => Three.WithSource(probed with { Id = 7 }));
    }
}
