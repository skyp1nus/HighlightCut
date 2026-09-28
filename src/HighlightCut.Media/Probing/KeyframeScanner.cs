using System.Globalization;
using HighlightCut.Media.Tools;

namespace HighlightCut.Media.Probing;

/// <summary>
/// Lists the keyframes of the video stream from packet flags (no decoding). An MP4 or MOV says in its index which
/// samples are keyframes, so there ffmpeg's demuxer skips the others (<c>-discard nokey</c>) and only the keyframes,
/// a few percent of the file, are read. Other files are read through with ffprobe. It runs in the background and
/// is cached per file.
/// </summary>
public static class KeyframeScanner
{
    /// <summary>ffmpeg's AV_NOPTS_VALUE, as framecrc prints a missing time.</summary>
    private const long NoTime = long.MinValue;

    /// <summary>Keyframe times in seconds from the file start (start time subtracted), sorted.</summary>
    /// <param name="progress">Fraction of the file scanned, 0..1.</param>
    public static async Task<double[]> ScanAsync(MediaInfo info, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (info.Video is null)
            return [];
        if (CanSkipToKeyframes(info)
            && await ReadKeyframesOnlyAsync(info, progress, cancellationToken).ConfigureAwait(false) is { } keyframes)
            return keyframes;
        return await ReadThroughAsync(info, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// MP4 and MOV without B-frames. With B-frames ffmpeg's MOV demuxer (6.x) gets the presentation times wrong once
    /// it skips samples, so those files are read through.
    /// </summary>
    public static bool CanSkipToKeyframes(MediaInfo info) =>
        info.Family == ContainerFamily.Mov && info.Video is { HasBFrames: false };

    /// <summary>ffmpeg arguments: the video stream's keyframe packets, copied into <c>framecrc</c>'s one line per packet.</summary>
    public static IReadOnlyList<string> KeyframeOnlyArguments(MediaInfo info) =>
    [
        "-v", "error", "-discard", "nokey", "-copyts", "-i", info.Path,
        "-map", "0:" + info.Video!.Index.ToString(CultureInfo.InvariantCulture), "-c", "copy", "-f", "framecrc", "-",
    ];

    /// <summary>
    /// The keyframes through <see cref="KeyframeOnlyArguments"/>; null when the times cannot be trusted (a packet
    /// is shown before it is decoded, a B-frame the probe did not report) or ffmpeg fails.
    /// </summary>
    internal static async Task<double[]?> ReadKeyframesOnlyAsync(MediaInfo info, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var times = new List<double>();
        (long Num, long Den) timeBase = (0, 0);
        bool reordered = false;
        var report = new Reporter(info, progress);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await ToolProcess.ReadLinesAsync("ffmpeg", KeyframeOnlyArguments(info), line =>
            {
                if (TimeBase(line) is { } tb)
                    timeBase = tb;
                if (ParseFrameLine(line) is not { } packet)
                    return;
                if (packet.Pts != packet.Dts)
                {
                    reordered = true;
                    stop.Cancel();
                    return;
                }
                if (timeBase.Den == 0)
                    return;
                // Rounded as ffprobe prints times, so both ways give the same keyframes.
                double t = Math.Round((double)packet.Pts * timeBase.Num / timeBase.Den, 6) - info.StartTime;
                if (packet.IsKey)
                    times.Add(t);
                report.At(t);
            }, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (reordered && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (MediaToolException)
        {
            return null;
        }
        if (reordered || timeBase.Den == 0)
            return null;
        progress?.Report(1);
        return Sorted(times);
    }

    /// <summary>Every packet of the video stream with ffprobe.</summary>
    internal static async Task<double[]> ReadThroughAsync(MediaInfo info, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var times = new List<double>();
        var report = new Reporter(info, progress);
        await ToolProcess.ReadLinesAsync("ffprobe",
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,dts_time,flags", "-of", "csv=p=0", "-i", info.Path],
            line =>
            {
                if (ParsePacket(line) is not { } packet)
                    return;
                double t = packet.Time - info.StartTime;
                if (packet.IsKey)
                    times.Add(t);
                report.At(t);
            }, cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
        return Sorted(times);
    }

    private static double[] Sorted(List<double> times)
    {
        times.Sort();
        return [.. times.Distinct()];
    }

    /// <summary>
    /// Parses one <c>pts_time,dts_time,flags</c> line. Returns null for lines without a usable time.
    /// Discarded packets ('D') are skipped.
    /// </summary>
    public static (double Time, bool IsKey)? ParsePacket(string line)
    {
        var parts = line.Split(',');
        if (parts.Length < 3)
            return null;
        string flags = parts[2];
        if (flags.Contains('D', StringComparison.Ordinal))
            return null;
        if (!TryTime(parts[0], out double t) && !TryTime(parts[1], out t))
            return null;
        return (t, flags.StartsWith('K'));
    }

    /// <summary>
    /// Parses one packet line of <c>framecrc</c>: "0, dts, pts, duration, size, 0xcrc", then ", F=0x…" when the flags are
    /// anything but "key" alone, and side data. Returns null for headers and lines without a time; discarded packets
    /// are skipped. A missing pts is taken from the dts.
    /// </summary>
    public static (long Dts, long Pts, bool IsKey)? ParseFrameLine(string line)
    {
        if (line.StartsWith('#'))
            return null;
        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 6 || !TryTicks(parts[1], out long dts) || !TryTicks(parts[2], out long pts))
            return null;
        int flags = 1;
        foreach (string part in parts.Skip(6))
        {
            if (part.StartsWith("F=0x", StringComparison.Ordinal)
                && int.TryParse(part.AsSpan(4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int f))
                flags = f;
        }
        // AV_PKT_FLAG_DISCARD: a packet the demuxer only passed on for decoding the next one.
        if ((flags & 4) != 0)
            return null;
        if (pts == NoTime)
            pts = dts;
        if (pts == NoTime)
            return null;
        return (dts == NoTime ? pts : dts, pts, (flags & 1) != 0);
    }

    /// <summary>The seconds per tick, as a fraction, in framecrc's header line "#tb 0: 1/15360".</summary>
    public static (long Num, long Den)? TimeBase(string line)
    {
        const string Prefix = "#tb 0:";
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var parts = line[Prefix.Length..].Trim().Split('/');
        return parts.Length == 2 && TryTicks(parts[0], out long num) && TryTicks(parts[1], out long den) && num > 0 && den > 0
            ? (num, den)
            : null;
    }

    private static bool TryTime(string text, out double t) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out t);

    private static bool TryTicks(string text, out long ticks) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out ticks);

    /// <summary>Reports the part of the file scanned, in steps of at least 1 %.</summary>
    private sealed class Reporter(MediaInfo info, IProgress<double>? progress)
    {
        private double _last;

        public void At(double time)
        {
            double f = info.Duration > 0 ? Math.Clamp(time / info.Duration, 0, 1) : 0;
            if (progress is null || f - _last < 0.01)
                return;
            _last = f;
            progress.Report(f);
        }
    }
}
