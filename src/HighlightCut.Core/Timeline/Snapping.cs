namespace HighlightCut.Core.Timeline;

/// <summary>Snapping of source times to keyframes and frame boundaries.</summary>
public static class Snapping
{
    /// <summary>
    /// The keyframe nearest to <paramref name="time"/> if it is within <paramref name="threshold"/> seconds,
    /// otherwise <paramref name="time"/> itself. <paramref name="keyframes"/> must be sorted.
    /// </summary>
    public static double ToNearest(IReadOnlyList<double> keyframes, double time, double threshold)
    {
        if (keyframes.Count == 0 || threshold <= 0)
            return time;
        int i = LowerBound(keyframes, time);
        double best = time, bestDistance = threshold;
        for (int j = Math.Max(0, i - 1); j <= Math.Min(keyframes.Count - 1, i); j++)
        {
            double d = Math.Abs(keyframes[j] - time);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = keyframes[j];
            }
        }
        return best;
    }

    /// <summary>The last keyframe at or before <paramref name="time"/>, or null if there is none.</summary>
    public static double? AtOrBefore(IReadOnlyList<double> keyframes, double time)
    {
        int i = LowerBound(keyframes, time + 1e-9);
        return i > 0 ? keyframes[i - 1] : null;
    }

    /// <summary>The first keyframe after <paramref name="time"/>, or null if there is none.</summary>
    public static double? After(IReadOnlyList<double> keyframes, double time)
    {
        int i = LowerBound(keyframes, time + 1e-9);
        return i < keyframes.Count ? keyframes[i] : null;
    }

    /// <summary>Rounds to the nearest frame start for a constant frame rate.</summary>
    public static double ToFrame(double time, double frameRate) =>
        frameRate > 0 ? Math.Round(time * frameRate) / frameRate : time;

    /// <summary>Index of the first element that is not less than <paramref name="value"/>.</summary>
    private static int LowerBound(IReadOnlyList<double> sorted, double value)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (sorted[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}
