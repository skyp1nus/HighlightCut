using System.Globalization;

namespace HighlightCut.Core.Time;

/// <summary>
/// Text formats for times shown in the UI. All values are seconds on the source timeline.
/// Milliseconds are rounded half away from zero.
/// </summary>
public static class TimeFormat
{
    /// <summary>Full timecode, <c>HH:MM:SS.mmm</c> (player readout).</summary>
    public static string Timecode(double seconds)
    {
        long ms = ToMilliseconds(seconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");
    }

    /// <summary>Minutes and seconds, <c>MM:SS.mmm</c> (clip start and end, playhead label).</summary>
    public static string MinutesSeconds(double seconds)
    {
        long ms = ToMilliseconds(seconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"{ms / 60_000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");
    }

    /// <summary>Duration, <c>M:SS.mmm</c> (clip durations, output total).</summary>
    public static string Duration(double seconds)
    {
        long ms = ToMilliseconds(seconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"{ms / 60_000}:{ms / 1000 % 60:00}.{ms % 1000:000}");
    }

    /// <summary>
    /// Compact duration for clip lists: <c>M:SS.mmm</c> from one minute up, otherwise <c>S.mmm s</c>
    /// (e.g. "1:43.440", "33.200 s").
    /// </summary>
    public static string ShortDuration(double seconds)
    {
        long ms = ToMilliseconds(seconds);
        return ms >= 60_000
            ? string.Create(CultureInfo.InvariantCulture, $"{ms / 60_000}:{ms / 1000 % 60:00}.{ms % 1000:000}")
            : string.Create(CultureInfo.InvariantCulture, $"{ms / 1000}.{ms % 1000:000} s");
    }

    /// <summary>Whole seconds, <c>M:SS</c>, truncated (source length, legend totals).</summary>
    public static string WholeSeconds(double seconds)
    {
        long s = (long)Math.Floor(Math.Max(0, seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{s / 60}:{s % 60:00}");
    }

    /// <summary>Whole seconds, <c>MM:SS</c>, rounded (elapsed and remaining export time).</summary>
    public static string Clock(double seconds)
    {
        long s = (long)Math.Round(Math.Max(0, seconds), MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{s / 60:00}:{s % 60:00}");
    }

    /// <summary>
    /// Parses <c>HH:MM:SS.mmm</c>, <c>MM:SS.mmm</c>, <c>SS.mmm</c> or plain seconds.
    /// </summary>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] parts = text.Trim().Split(':');
        if (parts.Length > 3)
            return false;

        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            var style = last ? NumberStyles.AllowDecimalPoint : NumberStyles.None;
            if (!double.TryParse(parts[i], style, CultureInfo.InvariantCulture, out double v) || v < 0)
                return false;
            if (i > 0 && v >= 60)
                return false;
            total = total * 60 + v;
        }

        seconds = total;
        return true;
    }

    private static long ToMilliseconds(double seconds) =>
        (long)Math.Round(Math.Max(0, seconds) * 1000, MidpointRounding.AwayFromZero);
}
