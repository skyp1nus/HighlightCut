using HighlightCut.Core.Timeline;
using HighlightCut.Media.Probing;

namespace HighlightCut.Media.Export;

/// <summary>Where a stream-copy cut starts and what to pass to ffmpeg for it.</summary>
/// <param name="EffectiveStart">Where the output really starts: the keyframe at or before the requested in-point.</param>
/// <param name="SeekTo">Value for <c>-ss</c> (before <c>-i</c>) that makes ffmpeg start at <see cref="EffectiveStart"/>.</param>
/// <param name="Duration">Value for <c>-t</c>: from <see cref="SeekTo"/> to the out-point.</param>
/// <param name="IsKnown">False if keyframes were not available, so the real start is not known in advance.</param>
public readonly record struct LosslessCut(double EffectiveStart, double SeekTo, double Duration, bool IsKnown)
{
    /// <summary>Length of the output, including the lead-in before the requested in-point.</summary>
    public double OutputDuration(double end) => end - EffectiveStart;
}

/// <summary>
/// Computes stream-copy cut points. With <c>-ss</c> before <c>-i</c> and <c>-c copy</c>, ffmpeg starts
/// at a keyframe, so a lossless cut starts at the keyframe at or before the in-point (nothing is lost;
/// a little lead-in is added). The out-point is packet-accurate.
/// </summary>
public static class CutPlanner
{
    /// <summary>
    /// For demuxers that do not seek by presentation time (Matroska and most others), ffmpeg seeks
    /// 3/23 s earlier than asked when the video has B-frames.
    /// </summary>
    public const double NonPtsSeekBackoff = 3.0 / 23.0;

    public static LosslessCut Plan(double start, double end, IReadOnlyList<double> keyframes, ContainerFamily family,
        bool hasBFrames, double frameDuration)
    {
        if (end <= start)
            throw new ArgumentException("The out-point must be after the in-point.", nameof(end));
        if (keyframes.Count == 0)
            return new LosslessCut(start, start, end - start, IsKnown: false);

        // A clip that starts before the first keyframe can only start at that keyframe.
        double k = Snapping.AtOrBefore(keyframes, start) ?? keyframes[0];
        double next = Snapping.After(keyframes, k) ?? double.PositiveInfinity;

        double delta = family switch
        {
            ContainerFamily.Mov => Math.Min(0.001, frameDuration / 4),
            // No index: seeking is approximate anyway; stay as close to the keyframe as possible.
            ContainerFamily.MpegTs => 0.001,
            _ when !hasBFrames => Math.Min(0.001, frameDuration / 4),
            _ => NonPtsSeekBackoff + Math.Min(0.02, Math.Max(0, next - k) / 2),
        };

        double seek = k + delta;
        // Very short clips: never seek past the out-point.
        if (seek >= end - frameDuration / 2)
            seek = k + Math.Min(0.001, frameDuration / 4);
        return new LosslessCut(k, seek, end - seek, IsKnown: family != ContainerFamily.MpegTs);
    }
}
