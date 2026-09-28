using HighlightCut.Media.Export;
using HighlightCut.Media.Probing;

namespace HighlightCut.Media.Tests.Export;

public class CutPlannerTests
{
    private static readonly double[] Keyframes = [0, 2.002, 4.004, 6.006, 8.008];
    private const double Frame = 1 / 29.97;

    [Fact]
    public void Mp4_cut_starts_at_the_keyframe_before_the_in_point()
    {
        var cut = CutPlanner.Plan(3.0, 5.5, Keyframes, ContainerFamily.Mov, hasBFrames: true, Frame);
        Assert.Equal(2.002, cut.EffectiveStart);
        Assert.Equal(2.003, cut.SeekTo, 9);
        Assert.Equal(5.5 - 2.003, cut.Duration, 9);
        Assert.Equal(5.5 - 2.002, cut.OutputDuration(5.5), 9);
        Assert.True(cut.IsKnown);
    }

    [Fact]
    public void An_in_point_on_a_keyframe_needs_no_lead_in()
    {
        var cut = CutPlanner.Plan(4.004, 5, Keyframes, ContainerFamily.Mov, hasBFrames: true, Frame);
        Assert.Equal(4.004, cut.EffectiveStart);
    }

    [Fact]
    public void Matroska_with_b_frames_compensates_for_ffmpegs_seek_backoff()
    {
        var cut = CutPlanner.Plan(3.0, 5.5, Keyframes, ContainerFamily.Matroska, hasBFrames: true, Frame);
        Assert.Equal(2.002, cut.EffectiveStart);
        // ffmpeg will seek to SeekTo - 3/23, which must land in [K, next K).
        double landing = cut.SeekTo - CutPlanner.NonPtsSeekBackoff;
        Assert.InRange(landing, 2.002, 4.004 - 1e-9);
    }

    [Fact]
    public void Short_gops_still_land_on_the_right_keyframe()
    {
        double[] kf = [0, 0.2, 0.4, 0.6];
        var cut = CutPlanner.Plan(0.45, 0.9, kf, ContainerFamily.Matroska, hasBFrames: true, 1 / 60.0);
        Assert.Equal(0.4, cut.EffectiveStart);
        Assert.InRange(cut.SeekTo - CutPlanner.NonPtsSeekBackoff, 0.4, 0.6 - 1e-9);
    }

    [Fact]
    public void Matroska_without_b_frames_uses_a_tiny_offset() =>
        Assert.Equal(2.003, CutPlanner.Plan(3, 5, Keyframes, ContainerFamily.Matroska, false, Frame).SeekTo, 9);

    [Fact]
    public void Transport_streams_are_marked_approximate()
    {
        var cut = CutPlanner.Plan(3, 5, Keyframes, ContainerFamily.MpegTs, true, Frame);
        Assert.Equal(2.003, cut.SeekTo, 9);
        Assert.False(cut.IsKnown);
    }

    [Fact]
    public void Without_keyframes_the_requested_times_are_used()
    {
        var cut = CutPlanner.Plan(3, 5, [], ContainerFamily.Mov, true, Frame);
        Assert.Equal((3.0, 3.0, 2.0, false), (cut.EffectiveStart, cut.SeekTo, cut.Duration, cut.IsKnown));
    }

    [Fact]
    public void A_clip_before_the_first_keyframe_starts_at_it()
    {
        var cut = CutPlanner.Plan(0.01, 1, [0.033, 2], ContainerFamily.Mov, false, Frame);
        Assert.Equal(0.033, cut.EffectiveStart);
    }

    [Fact]
    public void Very_short_clips_never_seek_past_the_out_point()
    {
        var cut = CutPlanner.Plan(2.05, 2.1, Keyframes, ContainerFamily.Matroska, true, Frame);
        Assert.True(cut.SeekTo < 2.1);
        Assert.True(cut.Duration > 0);
    }
}
