using System.Globalization;

namespace HighlightCut.App.Services;

/// <summary>
/// Estimates how long a job has left from its progress over time: the rate of the last few seconds (the parts of the
/// analysis finish at different times, so the early rate says little), smoothed so the figure counts down instead
/// of jumping.
/// </summary>
public sealed class TimeLeftEstimator
{
    /// <summary>Seconds of recent progress the rate is taken over.</summary>
    private const double Window = 4;

    /// <summary>Progress over less time than this says too little to estimate from.</summary>
    private const double MinSpan = 0.5;

    private readonly List<(double At, double Progress)> _samples = [];
    private double? _estimate;
    private double _estimateAt;

    public void Reset()
    {
        _samples.Clear();
        _estimate = null;
    }

    /// <summary>Seconds left after <paramref name="progress"/> (0..1) at <paramref name="at"/> seconds; null while there is too little to go on.</summary>
    public double? Add(double at, double progress)
    {
        if (progress >= 1)
            return 0;
        _samples.Add((at, progress));
        while (_samples.Count > 2 && at - _samples[1].At >= Window)
            _samples.RemoveAt(0);
        var (firstAt, firstProgress) = _samples[0];
        double span = at - firstAt, done = progress - firstProgress;
        double? counted = _estimate is { } old ? Math.Max(0, old - (at - _estimateAt)) : null;
        if (span < MinSpan || done <= 0)
            return counted;
        double measured = (1 - progress) * span / done;
        // The last estimate counted down to now, moved a third of the way to what the rate says.
        _estimate = counted is { } c ? c * 2 / 3 + measured / 3 : measured;
        _estimateAt = at;
        return _estimate;
    }

    /// <summary>"about 25 s left", "about 3 min left", "almost done", or "estimating time left" for null.</summary>
    public static string Format(double? secondsLeft)
    {
        var c = CultureInfo.InvariantCulture;
        return secondsLeft switch
        {
            null => "estimating time left",
            < 3 => "almost done",
            < 10 and var s => $"about {Math.Ceiling(s).ToString(c)} s left",
            < 60 and var s => $"about {(Math.Ceiling(s / 5) * 5).ToString(c)} s left",
            var s => $"about {Math.Ceiling(s.Value / 60).ToString(c)} min left",
        };
    }
}
