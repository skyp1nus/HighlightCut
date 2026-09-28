using System.Globalization;
using HighlightCut.Media.Tools;

namespace HighlightCut.Media.Export;

/// <summary>A hardware video encoder ffmpeg can re-encode with instead of the CPU.</summary>
/// <param name="Name">"NVIDIA NVENC", as Settings → Export shows it.</param>
/// <param name="Suffix">ffmpeg's encoder suffix: h264_<c>nvenc</c>, hevc_<c>nvenc</c>.</param>
public sealed record GpuEncoder(string Name, string Suffix)
{
    public static GpuEncoder Nvenc { get; } = new("NVIDIA NVENC", "nvenc");
    public static GpuEncoder Amf { get; } = new("AMD AMF", "amf");
    public static GpuEncoder QuickSync { get; } = new("Intel Quick Sync", "qsv");
    public static GpuEncoder VideoToolbox { get; } = new("Apple VideoToolbox", "videotoolbox");

    /// <summary>In the order they are tried: a dedicated GPU before an integrated one.</summary>
    public static IReadOnlyList<GpuEncoder> All { get; } = [Nvenc, Amf, QuickSync, VideoToolbox];

    /// <summary>ffmpeg's encoder for <paramref name="video"/>: h264_nvenc for libx264, hevc_nvenc for libx265.</summary>
    public string CodecFor(VideoEncoding video) => (IsHevc(video) ? "hevc_" : "h264_") + Suffix;

    /// <summary>
    /// The video codec arguments: constant quality near <paramref name="video"/>'s CRF, a faster preset for the fast
    /// one, and 8-bit 4:2:0 (nv12), which every one of these encoders takes.
    /// </summary>
    public string Arguments(VideoEncoding video)
    {
        string q = video.Crf.ToString(CultureInfo.InvariantCulture);
        bool fast = video.Preset is "ultrafast" or "superfast" or "veryfast" or "faster" or "fast";
        string quality = Suffix switch
        {
            "nvenc" => $"-preset {(fast ? "p3" : "p5")} -rc vbr -cq {q} -b:v 0",
            "amf" => $"-quality {(fast ? "speed" : "quality")} -rc cqp -qp_i {q} -qp_p {q}",
            "qsv" => $"-preset {(fast ? "veryfast" : "medium")} -global_quality {q}",
            // VideoToolbox's quality goes from 1 to 100, higher is better: CRF 18 is about 65, CRF 23 about 55.
            _ => "-q:v " + Math.Clamp(101 - 2 * video.Crf, 1, 100).ToString(CultureInfo.InvariantCulture),
        };
        return $"-c:v {CodecFor(video)} {quality} -pix_fmt nv12";
    }

    internal static bool IsHevc(VideoEncoding video) => video.Codec == "libx265";
}

/// <summary>The GPU encoder that works on this computer, and whether it also encodes H.265.</summary>
public sealed record GpuEncoderSupport(GpuEncoder Encoder, bool Hevc)
{
    /// <summary>The encoder for <paramref name="video"/>, or null for the CPU (H.265 on a GPU that has no HEVC encoder).</summary>
    public GpuEncoder? For(VideoEncoding video) => GpuEncoder.IsHevc(video) && !Hevc ? null : Encoder;
}

/// <summary>
/// Finds a GPU encoder: ffmpeg lists the ones it was built with, but most builds list NVENC, AMF and Quick Sync
/// whatever the hardware, so each is tried on a few frames with the arguments an export would use.
/// </summary>
public static class GpuEncoderProbe
{
    /// <summary>The first encoder that works, or null when none does (or ffmpeg is missing).</summary>
    /// <param name="ffmpeg">"ffmpeg" (found with <see cref="NativeTools"/>) or a full path.</param>
    public static async Task<GpuEncoderSupport?> DetectAsync(string ffmpeg = "ffmpeg", CancellationToken cancellationToken = default)
    {
        string encoders;
        try
        {
            encoders = await ToolProcess.ReadAllTextAsync(ffmpeg, ["-hide_banner", "-encoders"], cancellationToken).ConfigureAwait(false);
        }
        catch (MediaToolException)
        {
            return null;
        }
        var listed = ListedEncoders(encoders);
        foreach (var encoder in GpuEncoder.All)
        {
            if (!listed.Contains(encoder.CodecFor(VideoEncoding.H264Quality))
                || !await WorksAsync(ffmpeg, encoder, VideoEncoding.H264Quality, cancellationToken).ConfigureAwait(false))
                continue;
            bool hevc = listed.Contains(encoder.CodecFor(VideoEncoding.H265))
                && await WorksAsync(ffmpeg, encoder, VideoEncoding.H265, cancellationToken).ConfigureAwait(false);
            return new GpuEncoderSupport(encoder, hevc);
        }
        return null;
    }

    /// <summary>The encoder names in <c>ffmpeg -encoders</c>: " V....D h264_nvenc  NVIDIA NVENC H.264 encoder".</summary>
    internal static HashSet<string> ListedEncoders(string encoders) =>
    [
        .. encoders.Split('\n')
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 3 && parts[0].Length == 6 && parts[0][0] == 'V' && parts[1] != "=")
            .Select(parts => parts[1]),
    ];

    /// <summary>A fraction of a second of a plain picture, encoded and thrown away.</summary>
    internal static IReadOnlyList<string> TestArguments(GpuEncoder encoder, VideoEncoding video) =>
    [
        "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=c=gray:s=640x360:r=30:d=0.2",
        .. encoder.Arguments(video).Split(' '), "-f", "null", "-",
    ];

    private static async Task<bool> WorksAsync(string ffmpeg, GpuEncoder encoder, VideoEncoding video, CancellationToken cancellationToken)
    {
        // A driver that hangs counts as not working.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await ToolProcess.RunAsync(ffmpeg, TestArguments(encoder, video), null, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (MediaToolException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
