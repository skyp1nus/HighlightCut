using HighlightCut.Core.Model;

namespace HighlightCut.Core.Tests.Model;

public class ProjectTests
{
    [Fact]
    public void Output_duration_counts_only_included_clips() =>
        Assert.Equal(452.0, Sample.Project.OutputDuration, 6);

    [Fact]
    public void Uncovered_ranges_are_the_gaps_between_clips_in_source_order()
    {
        var gaps = Sample.Project.UncoveredRanges();
        Assert.Equal(
        [
            new TimeRange(0, 12.04), new TimeRange(45.32, 118.6), new TimeRange(190.12, 242.88), new TimeRange(404.0, 495.2),
            new TimeRange(602.56, 640.0), new TimeRange(728.4, 750.0), new TimeRange(828.72, 872.48),
        ], gaps);
    }

    [Fact]
    public void Excluded_duration_adds_gaps_and_excluded_clips() =>
        Assert.Equal(872.48 - 452.0, Sample.Project.ExcludedDuration, 6);

    [Fact]
    public void Overlapping_clips_do_not_create_negative_gaps()
    {
        var p = Sample.Project with { Clips = [new Clip(1, "a", 10, 50), new Clip(2, "b", 20, 30), new Clip(3, "c", 40, 60)] };
        Assert.Equal([new TimeRange(0, 10), new TimeRange(60, 872.48)], p.UncoveredRanges());
    }

    [Fact]
    public void Tiny_gaps_are_ignored()
    {
        var p = Sample.Project with { Clips = [new Clip(1, "a", 0.1, 50), new Clip(2, "b", 50.2, 872.4)] };
        Assert.Empty(p.UncoveredRanges());
    }

    [Fact]
    public void Numbers_follow_output_order()
    {
        Assert.Equal(5, Sample.Project.NumberOf(6));
        Assert.Equal(6, Sample.Project.NumberOf(5));
        Assert.Equal(7, Sample.Project.NextClipId);
    }

    [Fact]
    public void ClipAt_returns_the_first_clip_in_output_order()
    {
        Assert.Equal(3, Sample.Project.ClipAt(301.42)!.Id);
        Assert.Null(Sample.Project.ClipAt(100));
    }
}
