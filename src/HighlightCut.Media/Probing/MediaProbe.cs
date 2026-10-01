using System.Globalization;
using System.Text.Json;
using HighlightCut.Media.Tools;

namespace HighlightCut.Media.Probing;

/// <summary>
/// Reads file information with <c>ffprobe -show_format -show_streams</c> (JSON). ffprobe is called
/// directly rather than through FFMpegCore's analysis, which truncates times to milliseconds and
/// does not expose start time, time base or B-frame information.
/// </summary>
public static class MediaProbe
{
    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The file does not exist.", path);
        string json = await ToolProcess.ReadAllTextAsync("ffprobe",
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-i", path],
            cancellationToken).ConfigureAwait(false);
        return Parse(json, path);
    }

    /// <summary>Container duration of a file, or null if ffprobe cannot tell.</summary>
    public static async Task<double?> ProbeDurationAsync(string path, CancellationToken cancellationToken = default)
    {
        string text = await ToolProcess.ReadAllTextAsync("ffprobe",
            ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", "-i", path], cancellationToken).ConfigureAwait(false);
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0 ? d : null;
    }

    /// <exception cref="MediaToolException">The output does not describe a usable media file.</exception>
    public static MediaInfo Parse(string json, string path)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var format))
            throw new MediaToolException("ffprobe did not recognise the file.");

        double startTime = Number(format, "start_time") ?? 0;
        VideoStreamInfo? video = null;
        var audio = new List<AudioStreamInfo>();
        var subtitles = new List<SubtitleStreamInfo>();
        double streamDuration = 0;

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                string type = Text(s, "codec_type") ?? "";
                int index = Int(s, "index") ?? 0;
                string codec = Text(s, "codec_name") ?? "unknown";
                streamDuration = Math.Max(streamDuration, Number(s, "duration") ?? 0);
                switch (type)
                {
                    case "video" when video is null && !IsAttachedPicture(s):
                        var (fps, fpsText) = FrameRate(s);
                        video = new VideoStreamInfo(index, codec, Int(s, "width") ?? 0, Int(s, "height") ?? 0, fps, fpsText,
                            (Int(s, "has_b_frames") ?? 0) > 0, Text(s, "pix_fmt"), Rotation(s))
                        {
                            FrameRateRational = RateText(s),
                            TimeBase = Text(s, "time_base"),
                        };
                        break;
                    case "audio":
                        audio.Add(new AudioStreamInfo(index, audio.Count, codec, Int(s, "channels") ?? 0,
                            (int)(Number(s, "sample_rate") ?? 0), TrackTitle(s), Tag(s, "language"))
                        {
                            ChannelLayout = Text(s, "channel_layout"),
                        });
                        break;
                    case "subtitle":
                        subtitles.Add(new SubtitleStreamInfo(index, codec, Tag(s, "language")));
                        break;
                }
            }
        }

        double duration = Number(format, "duration") ?? streamDuration;
        if (!(duration > 0))
            throw new MediaToolException("The file has no duration; it may not be a video.");
        if (video is null && audio.Count == 0)
            throw new MediaToolException("The file has no video or audio streams.");

        return new MediaInfo(path, Text(format, "format_name") ?? "", duration, startTime,
            (long)(Number(format, "bit_rate") ?? 0), (long)(Number(format, "size") ?? 0),
            video, [.. audio], [.. subtitles]);
    }

    /// <summary>
    /// Average frame rate, falling back to the base rate (r_frame_rate) when it is missing.
    /// Also returns a display text such as "29.97" or "25".
    /// </summary>
    internal static (double Fps, string Text) FrameRate(JsonElement stream)
    {
        double fps = Rational(Text(stream, "avg_frame_rate"));
        if (!(fps > 0) || fps > 1000)
            fps = Rational(Text(stream, "r_frame_rate"));
        if (!(fps > 0) || fps > 1000)
            return (0, "?");
        double rounded = Math.Round(fps, 2);
        return (fps, rounded.ToString(rounded == Math.Floor(rounded) ? "0" : "0.##", CultureInfo.InvariantCulture));
    }

    /// <summary>The rate <see cref="FrameRate"/> reads, as ffprobe wrote it ("30000/1001"); null when it has none.</summary>
    internal static string? RateText(JsonElement stream)
    {
        string? average = Text(stream, "avg_frame_rate");
        double fps = Rational(average);
        if (fps > 0 && fps <= 1000)
            return average;
        string? real = Text(stream, "r_frame_rate");
        fps = Rational(real);
        return fps > 0 && fps <= 1000 ? real : null;
    }

    internal static double Rational(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        int slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        if (!double.TryParse(text.AsSpan(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ||
            !double.TryParse(text.AsSpan(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || d == 0)
            return 0;
        return n / d;
    }

    /// <summary>Handler names muxers write by default; they say nothing about the track.</summary>
    private static readonly string[] GenericHandlers =
    [
        "SoundHandler", "VideoHandler", "SubtitleHandler", "Core Media Audio", "Core Media Video", "Apple Sound Media Handler",
        "Mainconcept MP4 Sound Media Handler", "GPAC ISO Audio Handler", "ISO Media file produced by Google Inc.",
        "L-SMASH Audio Handler", "Bento4 Sound Handler", "Stereo", "Mono",
    ];

    /// <summary>
    /// The stream's title. MP4/MOV cannot store a title per stream, so tools put the track name in the
    /// handler name instead; it is used unless it is a muxer default such as "SoundHandler".
    /// </summary>
    private static string? TrackTitle(JsonElement s)
    {
        if (Tag(s, "title") is { Length: > 0 } title)
            return title;
        string? handler = Tag(s, "handler_name")?.Trim();
        return string.IsNullOrEmpty(handler) || GenericHandlers.Contains(handler, StringComparer.OrdinalIgnoreCase) ? null : handler;
    }

    private static bool IsAttachedPicture(JsonElement s) =>
        s.TryGetProperty("disposition", out var d) && Int(d, "attached_pic") == 1;

    private static int Rotation(JsonElement s)
    {
        if (s.TryGetProperty("side_data_list", out var list))
        {
            foreach (var item in list.EnumerateArray())
            {
                if (Number(item, "rotation") is { } r)
                    return (int)Math.Round(r);
            }
        }
        return Tag(s, "rotate") is { } tag && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rot) ? rot : 0;
    }

    private static string? Tag(JsonElement s, string name)
    {
        if (!s.TryGetProperty("tags", out var tags))
            return null;
        foreach (var p in tags.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return p.Value.GetString();
        }
        return null;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    private static double? Number(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
    }

    private static int? Int(JsonElement e, string name) => Number(e, name) is { } d ? (int)d : null;
}
