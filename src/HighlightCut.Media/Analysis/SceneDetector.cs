using System.Globalization;
using HighlightCut.Media.Ffmpeg;
using HighlightCut.Media.Probing;
using HighlightCut.Media.Tools;

namespace HighlightCut.Media.Analysis;

/// <summary>Scene changes found at a sensitivity.</summary>
/// <param name="Changes">Times (seconds) where a new scene starts.</param>
/// <param name="IsComplete">False while detection is still running; the changes then cover the scanned part.</param>
/// <param name="Progress">Part of the video scanned, 0..1.</param>
public sealed record SceneAnalysis(IReadOnlyList<double> Changes, double Threshold, bool IsComplete, double Progress);

/// <summary>
/// How much each video frame differs from the one before, at a fixed frame rate, filled progressively while
/// ffmpeg decodes. Scores use the scale of ffmpeg's <c>scdet</c> filter (0–100). Keeping the scores rather than
/// the scene changes lets the sensitivity change without decoding the video again.
/// </summary>
public sealed class SceneScores
{
    private readonly float[] _scores;
    private int _filled;

    public SceneScores(double rate, double duration)
    {
        Rate = rate;
        _scores = new float[(int)Math.Ceiling(Math.Max(0, duration) * rate) + 1];
    }

    internal SceneScores(double rate, float[] scores, int filled)
    {
        Rate = rate;
        _scores = scores;
        _filled = filled;
    }

    /// <summary>Frames per second the scores are taken at; frame <c>k</c> is at <c>k / Rate</c> seconds.</summary>
    public double Rate { get; }

    public int Capacity => _scores.Length;

    /// <summary>Frames scored so far.</summary>
    public int Filled => Volatile.Read(ref _filled);

    public bool IsComplete { get; internal set; }

    public double Progress => IsComplete ? 1 : Capacity == 0 ? 0 : Math.Min(1, (double)Filled / Capacity);

    /// <summary>Score of frame <paramref name="frame"/> against the frame before it.</summary>
    public float this[int frame] => _scores[frame];

    internal float[] RawScores => _scores;

    internal void Set(int frame, float score)
    {
        if (frame < _scores.Length)
            _scores[frame] = score;
    }

    internal void Publish(int filled) => Volatile.Write(ref _filled, Math.Min(filled, Capacity));

    public SceneAnalysis Analyse(double threshold = SceneDetector.DefaultThreshold, double minGap = SceneDetector.DefaultMinGap) =>
        new(Changes(threshold, minGap), threshold, IsComplete, Progress);

    /// <summary>
    /// Times (seconds) where a new scene starts: frames scoring at least <paramref name="threshold"/>. Of changes
    /// closer than <paramref name="minGap"/> (a flash, a fast wipe) only the strongest is kept.
    /// </summary>
    public IReadOnlyList<double> Changes(double threshold = SceneDetector.DefaultThreshold, double minGap = SceneDetector.DefaultMinGap)
    {
        int filled = Filled;
        var frames = new List<int>();
        for (int k = 1; k < filled; k++)
        {
            if (_scores[k] < threshold)
                continue;
            if (frames.Count > 0 && (k - frames[^1]) / Rate < minGap)
            {
                if (_scores[k] > _scores[frames[^1]])
                    frames[^1] = k;
                continue;
            }
            frames.Add(k);
        }
        return [.. frames.Select(k => Math.Round(k / Rate, 3))];
    }
}

/// <summary>
/// Scene change detection: ffmpeg decodes the video to tiny grey frames at a fixed rate and each frame is
/// compared with the one before, the way ffmpeg's <c>scdet</c> does: the mean difference, but only where it
/// jumps from one frame to the next, so steady motion (panning, scrolling) does not count as a cut.
/// </summary>
public static class SceneDetector
{
    /// <summary>Default sensitivity: ffmpeg <c>scdet</c>'s default threshold. Lower finds more changes.</summary>
    public const double DefaultThreshold = 10;

    /// <summary>Changes closer together than this (seconds) count as one.</summary>
    public const double DefaultMinGap = 0.5;

    public const int Width = 64;
    public const int Height = 36;

    /// <summary>The video's own frame rate (so every frame is compared), 30 fps if it is unknown or unusual.</summary>
    public static double RateFor(MediaInfo info) =>
        info.Video?.FrameRate is > 1 and <= 60 and var fps ? fps : 30;

    public static SceneScores Create(MediaInfo info) => new(RateFor(info), info.Duration);

    /// <summary>
    /// ffmpeg arguments: frames at a fixed rate from the start of the file (as keyframe times are counted), each
    /// shrunk to <see cref="Width"/>×<see cref="Height"/> grey, as raw bytes on stdout. Decoding is on the GPU when
    /// there is one, otherwise on every core: ffmpeg runs below normal priority, so playback and the editor still come
    /// first (<see cref="ToolProcess"/>).
    /// </summary>
    public static IReadOnlyList<string> Arguments(MediaInfo info, double rate) =>
    [
        "-v", "error", .. FfmpegText.GpuDecoding, "-i", info.Path,
        "-map", "0:" + info.Video!.Index.ToString(CultureInfo.InvariantCulture), "-an", "-sn", "-dn",
        "-vf", string.Create(CultureInfo.InvariantCulture,
            $"fps=fps={rate:0.######}:start_time=0:round=near,scale={Width}:{Height}:flags=area,format=gray"),
        "-f", "rawvideo", "pipe:1",
    ];

    public static async Task DetectAsync(MediaInfo info, SceneScores target, Action? onChunk = null,
        CancellationToken cancellationToken = default)
    {
        if (info.Video is null)
        {
            target.IsComplete = true;
            return;
        }
        const int FrameBytes = Width * Height;
        await ToolProcess.RunAsync("ffmpeg", Arguments(info, target.Rate), async (stdout, ct) =>
        {
            var previous = new byte[FrameBytes];
            var frame = new byte[FrameBytes];
            double previousMafd = 0;
            var lastPublish = DateTime.UtcNow;
            for (int k = 0; ; k++)
            {
                int filled = 0;
                while (filled < FrameBytes)
                {
                    int read = await stdout.ReadAsync(frame.AsMemory(filled), ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        target.Publish(k);
                        return;
                    }
                    filled += read;
                }
                double mafd = k == 0 ? 0 : MeanDifference(previous, frame);
                target.Set(k, k == 0 ? 0 : (float)Score(mafd, previousMafd));
                previousMafd = mafd;
                (previous, frame) = (frame, previous);
                if ((DateTime.UtcNow - lastPublish).TotalMilliseconds > 250)
                {
                    target.Publish(k + 1);
                    onChunk?.Invoke();
                    lastPublish = DateTime.UtcNow;
                }
            }
        }, cancellationToken).ConfigureAwait(false);
        target.IsComplete = true;
        onChunk?.Invoke();
    }

    /// <summary>Mean absolute difference of two grey frames, as a percentage of full scale (0–100).</summary>
    public static double MeanDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return sum * 100.0 / (a.Length * 256.0);
    }

    /// <summary><c>scdet</c>'s score: the difference, but no more than its jump from the previous frame's.</summary>
    public static double Score(double mafd, double previousMafd) => Math.Clamp(Math.Min(mafd, Math.Abs(mafd - previousMafd)), 0, 100);
}
