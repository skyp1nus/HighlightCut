using System.Globalization;

namespace OurCut.Media.Ffmpeg;

/// <summary>Formatting helpers for values passed on the ffmpeg/ffprobe command line.</summary>
public static class FfmpegText
{
    /// <summary>
    /// Input options that decode on the GPU when there is one (D3D11/DXVA2 on Windows, VideoToolbox on macOS, VAAPI/VDPAU on
    /// Linux) and on the CPU otherwise: ffmpeg falls back by itself. Frames come back to memory for the filters.
    /// </summary>
    public static IReadOnlyList<string> GpuDecoding { get; } = ["-hwaccel", "auto"];

    /// <summary>
    /// Seconds with six decimals and an invariant decimal point, e.g. <c>12.040000</c>.
    /// ffmpeg accepts this for -ss, -t and -to without the millisecond truncation of TimeSpan.
    /// </summary>
    public static string Seconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Time must be a finite number.");
        return Math.Max(0, seconds).ToString("0.000000", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// libavfilter's <c>volume</c> filter for a gain in dB, e.g. <c>volume=-6dB</c>; <c>volume=0</c> at
    /// <see cref="OurCut.Core.Model.TrackMix.MinGainDb"/> and below, which means silent.
    /// </summary>
    public static string VolumeFilter(double gainDb) =>
        gainDb <= OurCut.Core.Model.TrackMix.MinGainDb
            ? "volume=0"
            : "volume=" + gainDb.ToString("0.##", CultureInfo.InvariantCulture) + "dB";
}
