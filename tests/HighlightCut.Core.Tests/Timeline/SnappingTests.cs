using HighlightCut.Core.Timeline;

namespace HighlightCut.Core.Tests.Timeline;

public class SnappingTests
{
    private static readonly double[] Keyframes = [0, 2.002, 4.004, 6.006];

    [Theory]
    [InlineData(2.3, 0.5, 2.002)]
    [InlineData(3.2, 0.5, 3.2)]
    [InlineData(3.9, 0.5, 4.004)]
    [InlineData(-0.1, 0.5, 0)]
    [InlineData(7, 1.5, 6.006)]
    [InlineData(2.3, 0, 2.3)]
    public void ToNearest_snaps_within_the_threshold(double t, double threshold, double expected) =>
        Assert.Equal(expected, Snapping.ToNearest(Keyframes, t, threshold));

    [Fact]
    public void ToNearest_without_keyframes_returns_the_time() =>
        Assert.Equal(5, Snapping.ToNearest([], 5, 1));

    [Theory]
    [InlineData(4.004, 4.004)]
    [InlineData(5, 4.004)]
    [InlineData(1.9, 0)]
    public void AtOrBefore_finds_the_keyframe_a_lossless_cut_starts_from(double t, double expected) =>
        Assert.Equal(expected, Snapping.AtOrBefore(Keyframes, t));

    [Fact]
    public void AtOrBefore_and_After_return_null_past_the_ends()
    {
        Assert.Null(Snapping.AtOrBefore(Keyframes, -1));
        Assert.Null(Snapping.After(Keyframes, 6.006));
        Assert.Equal(2.002, Snapping.After(Keyframes, 0));
    }

    [Fact]
    public void ToFrame_rounds_to_frame_starts() =>
        Assert.Equal(1001 / 29.97, Snapping.ToFrame(33.41, 29.97), 9);
}
