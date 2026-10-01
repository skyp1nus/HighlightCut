using System.Globalization;
using HighlightCut.Core.Model;
using HighlightCut.Media.Probing;

namespace HighlightCut.Media.Export;

/// <summary>A video an export cuts from, with the keyframes its lossless cuts start on.</summary>
public sealed record ExportSource(MediaInfo Info, IReadOnlyList<double> Keyframes);

/// <summary>One audio track of an export that joins several videos: track N of every video.</summary>
/// <param name="Position">N, 0-based: the position among each video's audio streams (ffmpeg's <c>a:N</c>).</param>
/// <param name="SampleRate">What a re-encode resamples every video's track N to: that of the first video with the track.</param>
/// <param name="Channels">Likewise, its number of channels.</param>
public sealed record ExportLane(int Position, int SampleRate, int Channels)
{
    /// <summary>The channel layout a re-encode gives the track, e.g. "stereo".</summary>
    public string Layout => ExportLayout.LayoutName(Channels);
}

/// <summary>What one video plays on one output track.</summary>
/// <param name="Stream">The video's track; null when it has no such track, which is then silent while it plays.</param>
/// <param name="GainDb">
/// Its volume: the video's own, or silent (<see cref="TrackMix.MinGainDb"/>) when only unmuted tracks are kept and it is
/// muted in this video while another video's is not.
/// </param>
public sealed record LaneInput(AudioStreamInfo? Stream, double GainDb);

/// <summary>
/// How the clips of several videos become one file: the picture of the first video (its size and frame rate, which a
/// re-encode scales, pads and resamples every video to) and the audio tracks, track N of each video on output track N.
/// </summary>
/// <param name="Video">The first video's picture; null when no video has one.</param>
/// <param name="Width">The output's width: the first video's as displayed, made even.</param>
/// <param name="FrameRate">For ffmpeg's <c>fps</c> filter, e.g. "30000/1001".</param>
/// <param name="Inputs">Per video id, what it plays on each of <paramref name="Lanes"/>.</param>
public sealed record ExportLayout(
    VideoStreamInfo? Video,
    int Width,
    int Height,
    string FrameRate,
    IReadOnlyList<ExportLane> Lanes,
    IReadOnlyDictionary<int, IReadOnlyList<LaneInput>> Inputs)
{
    /// <summary>
    /// Output tracks every video plays at the same volume, with that volume when it is not 0 dB: a lossless join applies
    /// it once, when the pieces are joined. The other tracks get each video's volume as its pieces are cut.
    /// </summary>
    public IReadOnlyList<(int Position, double GainDb)> SharedGains =>
        [.. Enumerable.Range(0, Lanes.Count).Where(IsShared).Select(k => (k, Inputs.Values.First()[k].GainDb)).Where(g => g.GainDb != 0)];

    /// <summary>Every video plays output track <paramref name="k"/> at the same volume.</summary>
    public bool IsShared(int k) => Inputs.Values.Select(v => v[k].GainDb).Distinct().Count() == 1;

    /// <param name="videos">The videos the export cuts from, in timeline order, by id.</param>
    public static ExportLayout Create(IReadOnlyList<(int Id, MediaInfo Info)> videos, ExportSettings settings)
    {
        var picture = videos.Select(v => v.Info.Video).FirstOrDefault(v => v is not null);
        var (w, h) = picture?.DisplaySize ?? (0, 0);
        string rate = picture?.FrameRateRational
            ?? (picture is { FrameRate: > 0 } p ? p.FrameRate.ToString("0.###", CultureInfo.InvariantCulture) : "30");

        int count = videos.Max(v => v.Info.Audio.Length);
        var positions = Enumerable.Range(0, count)
            .Where(k => settings.KeepAllTracks || videos.Any(v => Kept(v, k) is not null))
            .ToList();
        var lanes = positions.Select(k =>
        {
            var first = videos.Select(v => Track(v.Info, k)).First(a => a is not null)!;
            return new ExportLane(k, first.SampleRate > 0 ? first.SampleRate : 48000, first.Channels > 0 ? first.Channels : 2);
        }).ToList();
        var inputs = videos.ToDictionary(v => v.Id, v => (IReadOnlyList<LaneInput>)[.. positions.Select(k =>
        {
            var source = settings.ForSource(v.Id);
            return Track(v.Info, k) is not { } track ? new LaneInput(null, 0)
                : Kept(v, k) is null ? new LaneInput(track, TrackMix.MinGainDb)
                : new LaneInput(track, source.AudioGainsDb.GetValueOrDefault(track.Index));
        })]);
        return new ExportLayout(picture, Even(w), Even(h), rate, lanes, inputs);

        AudioStreamInfo? Kept((int Id, MediaInfo Info) video, int k) =>
            Track(video.Info, k) is { } track && (settings.KeepAllTracks || settings.ForSource(video.Id).AudioStreamIndexes.Contains(track.Index))
                ? track
                : null;
    }

    /// <summary>
    /// Why a lossless export cannot join these videos into one file, naming the videos that differ from the first and how;
    /// null when it can. Stream copy needs the same codecs, size, frame rate, pixel format and time base, and the same audio
    /// tracks (codec, sample rate, channels) in every video.
    /// </summary>
    public static string? LosslessProblem(IReadOnlyList<(int Id, MediaInfo Info)> videos, ExportSettings settings)
    {
        if (videos.Count < 2)
            return null;
        var layout = Create(videos, settings);
        var first = videos[0].Info;
        string firstName = Path.GetFileName(first.Path);
        var problems = new List<string>();
        foreach (var (id, info) in videos.Skip(1))
        {
            var diffs = new List<string>();
            if (first.Video is { } a && info.Video is { } b)
            {
                if (a.Codec != b.Codec)
                    diffs.Add($"{b.Codec} video instead of {a.Codec}");
                if ((a.Width, a.Height, a.Rotation) != (b.Width, b.Height, b.Rotation))
                    diffs.Add($"{Size(b)} instead of {Size(a)}");
                if ((a.FrameRateRational ?? a.FrameRateText) != (b.FrameRateRational ?? b.FrameRateText))
                    diffs.Add($"{b.FrameRateText} fps instead of {a.FrameRateText}");
                if (a.PixelFormat != b.PixelFormat)
                    diffs.Add($"{b.PixelFormat ?? "unknown"} pixels instead of {a.PixelFormat ?? "unknown"}");
                // A time base usually follows the frame rate: named only when nothing else tells the pictures apart.
                if (diffs.Count == 0 && a.TimeBase != b.TimeBase)
                    diffs.Add($"a time base of {b.TimeBase ?? "unknown"} instead of {a.TimeBase ?? "unknown"}");
            }
            else if (first.Video is not null || info.Video is not null)
            {
                diffs.Add(info.Video is null ? "no picture" : "a picture");
            }
            for (int k = 0; k < layout.Lanes.Count; k++)
            {
                var mine = layout.Inputs[id][k].Stream;
                var theirs = layout.Inputs[videos[0].Id][k].Stream;
                int n = layout.Lanes[k].Position + 1;
                if (mine is null && theirs is not null)
                    diffs.Add($"no audio track {n}");
                else if (mine is not null && theirs is null)
                    diffs.Add($"an audio track {n}");
                else if (mine is not null && theirs is not null && Sound(mine) != Sound(theirs))
                    diffs.Add($"audio track {n} in {Sound(mine)} instead of {Sound(theirs)}");
            }
            if (diffs.Count > 0)
                problems.Add($"{Path.GetFileName(info.Path)} has {And(diffs)}");
        }
        return problems.Count == 0
            ? null
            : $"Lossless can’t join these videos into one file: next to {firstName}, {string.Join("; ", problems)}.";

        static string Size(VideoStreamInfo v) => $"{v.Width}×{v.Height}" + (v.Rotation != 0 ? $" turned {v.Rotation}°" : "");

        static string Sound(AudioStreamInfo a) =>
            $"{a.Codec} {(a.SampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} kHz {a.ChannelLayout ?? LayoutName(a.Channels)}";

        static string And(List<string> items) =>
            items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
    }

    /// <summary>The audio stream at position <paramref name="k"/> among a file's audio streams.</summary>
    private static AudioStreamInfo? Track(MediaInfo info, int k) => k < info.Audio.Length ? info.Audio[k] : null;

    /// <summary>ffmpeg's name for a number of channels: "mono", "stereo", "5.1"…</summary>
    internal static string LayoutName(int channels) => channels switch
    {
        1 => "mono",
        3 => "2.1",
        4 => "quad",
        5 => "5.0",
        6 => "5.1",
        7 => "6.1",
        8 => "7.1",
        _ => "stereo",
    };

    private static int Even(int size) => Math.Max(2, size - size % 2);
}
