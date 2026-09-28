using System.Globalization;
using HighlightCut.Media.Ffmpeg;
using HighlightCut.Media.Probing;
using HighlightCut.Media.Tools;

namespace HighlightCut.Media.Previews;

/// <summary>A decoded video frame, 32-bit BGRA (opaque).</summary>
public sealed record ThumbnailFrame(double Time, int Width, int Height, byte[] Bgra);

/// <summary>
/// Extracts evenly spaced thumbnails in one ffmpeg pass. Only keyframes are decoded (<c>-skip_frame nokey</c>), which is
/// fast even for long 4K files, and from an MP4 or MOV only keyframes are read (<c>-discard nokey</c>: its index says
/// which samples they are).
/// </summary>
public static class ThumbnailExtractor
{
    public const int DefaultHeight = 90;

    /// <summary>Seconds between thumbnails: at most about 300 per file, at least 2 s apart.</summary>
    public static double IntervalFor(double duration) => Math.Max(2, duration / 300);

    /// <summary>Thumbnail size for a given height, keeping the display aspect ratio (even width).</summary>
    public static (int Width, int Height) SizeFor(VideoStreamInfo video, int height)
    {
        var (w, h) = video.DisplaySize;
        if (w <= 0 || h <= 0)
            return (height * 16 / 9 / 2 * 2, height);
        int width = (int)Math.Round((double)height * w / h / 2) * 2;
        return (Math.Max(2, width), height);
    }

    public static IReadOnlyList<string> Arguments(MediaInfo info, int width, int height, double interval) =>
    [
        "-v", "error", .. FfmpegText.GpuDecoding, "-discard", "nokey", "-skip_frame", "nokey", "-i", info.Path,
        "-map", "0:" + info.Video!.Index.ToString(CultureInfo.InvariantCulture), "-an", "-sn", "-dn",
        "-vf", string.Create(CultureInfo.InvariantCulture,
            $"fps=1/{interval:0.######}:round=near,scale={width}:{height}:flags=bilinear,format=bgra"),
        "-f", "rawvideo", "pipe:1",
    ];

    /// <summary>Calls <paramref name="onFrame"/> for each thumbnail as soon as it is decoded.</summary>
    public static async Task ExtractAsync(MediaInfo info, Action<ThumbnailFrame> onFrame, int height = DefaultHeight,
        double? interval = null, CancellationToken cancellationToken = default)
    {
        if (info.Video is null)
            return;
        var (w, h) = SizeFor(info.Video, height);
        double step = interval ?? IntervalFor(info.Duration);
        int frameBytes = w * h * 4;
        await ToolProcess.RunAsync("ffmpeg", Arguments(info, w, h, step), async (stdout, ct) =>
        {
            for (int k = 0; ; k++)
            {
                var frame = new byte[frameBytes];
                int filled = 0;
                while (filled < frameBytes)
                {
                    int read = await stdout.ReadAsync(frame.AsMemory(filled), ct).ConfigureAwait(false);
                    if (read == 0)
                        return;
                    filled += read;
                }
                onFrame(new ThumbnailFrame(Math.Min(k * step, info.Duration), w, h, frame));
            }
        }, cancellationToken).ConfigureAwait(false);
    }
}
