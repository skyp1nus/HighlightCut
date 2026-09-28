using HighlightCut.Core.Model;
using HighlightCut.Media.Analysis;
using HighlightCut.Media.Caching;
using HighlightCut.Media.Previews;
using HighlightCut.Media.Probing;
using HighlightCut.Media.Tools;

namespace HighlightCut.Media.Tests.Analysis;

public class SilenceDetectorTests
{
    /// <summary>A waveform with the given level (dBFS; null = digital silence) per 10 ms bucket range.</summary>
    private static WaveformData Wave(double duration, params (double From, double To, double? Db)[] parts)
    {
        var wave = new WaveformData(1, duration);
        foreach (var (from, to, db) in parts)
        {
            for (int b = (int)Math.Round(from * 100); b < (int)Math.Round(to * 100); b++)
                wave.Set(0, b, db is { } d ? (float)Math.Pow(10, d / 20) : 0);
        }
        wave.Publish((int)Math.Round(duration * 100));
        wave.IsComplete = true;
        return wave;
    }

    [Fact]
    public void Finds_pauses_at_least_the_minimum_length()
    {
        var wave = Wave(10, (0, 2, -12), (2, 4, null), (4, 6, -12), (6, 6.5, null), (6.5, 8, -12), (8, 10, null));

        var result = SilenceDetector.Find(wave);

        Assert.Equal([new TimeRange(2, 4), new TimeRange(8, 10)], result.Ranges);
        Assert.Equal(4, result.Total, 6);
        Assert.True(result.IsComplete);
        Assert.Empty(SilenceDetector.Find(wave, minDuration: 2.5).Ranges);
        Assert.Equal(3, SilenceDetector.Find(wave, minDuration: 0.3).Ranges.Count);
    }

    [Fact]
    public void The_automatic_level_follows_the_noise_floor()
    {
        // Room noise at −50 dB and speech at −15 dB: the pauses are noise, not digital silence.
        var wave = Wave(10, (0, 3, -15), (3, 5, -50), (5, 10, -15));

        var result = SilenceDetector.Find(wave);

        Assert.Equal(-50, result.NoiseFloorDb, 0);
        Assert.Equal(-38, result.ThresholdDb, 0);
        Assert.Equal([new TimeRange(3, 5)], result.Ranges);
        // A fixed level below the noise finds nothing.
        Assert.Empty(SilenceDetector.Find(wave, thresholdDb: -60).Ranges);
    }

    [Fact]
    public void Every_chosen_track_must_be_quiet()
    {
        var wave = new WaveformData(2, 4);
        for (int b = 0; b < 400; b++)
        {
            wave.Set(0, b, b is >= 100 and < 300 ? 0 : 0.3f);
            wave.Set(1, b, 0.3f);
        }
        wave.Publish(400);
        wave.IsComplete = true;

        Assert.Empty(SilenceDetector.Find(wave).Ranges);
        Assert.Equal([new TimeRange(1, 3)], SilenceDetector.Find(wave, streams: [0]).Ranges);
    }

    [Fact]
    public void A_silence_at_the_decoded_edge_waits_until_the_audio_is_complete()
    {
        var wave = new WaveformData(1, 10);
        for (int b = 0; b < 500; b++)
            wave.Set(0, b, b < 200 ? 0.3f : 0);
        wave.Publish(500);

        var partial = SilenceDetector.Find(wave);
        Assert.False(partial.IsComplete);
        Assert.Empty(partial.Ranges);

        wave.IsComplete = true;
        Assert.Equal([new TimeRange(2, 5)], SilenceDetector.Find(wave).Ranges);
    }

    [Fact]
    public void No_audio_means_no_silences()
    {
        var result = SilenceDetector.Find(new WaveformData(0, 10));
        Assert.Empty(result.Ranges);
        Assert.True(result.IsComplete);
    }

    [Theory]
    [InlineData(-90, -55)]
    [InlineData(-60, -48)]
    [InlineData(-30, -35)]
    public void The_automatic_level_stays_in_a_sensible_range(double floor, double expected) =>
        Assert.Equal(expected, SilenceDetector.AutoThresholdDb(floor));
}

public class SceneScoresTests
{
    private static SceneScores Scores(double rate, params float[] scores)
    {
        var s = new SceneScores(rate, scores.Length / rate);
        for (int k = 0; k < scores.Length; k++)
            s.Set(k, scores[k]);
        s.Publish(scores.Length);
        s.IsComplete = true;
        return s;
    }

    [Fact]
    public void Changes_are_frames_over_the_threshold()
    {
        var scores = Scores(10, 0, 1, 2, 30, 1, 0, 0, 0, 12, 0);
        Assert.Equal([0.3, 0.8], scores.Changes(minGap: 0));
        Assert.Equal([0.3], scores.Changes(threshold: 20, minGap: 0));
        Assert.Equal([0.2, 0.3, 0.8], scores.Changes(threshold: 2, minGap: 0));
    }

    [Fact]
    public void Of_changes_close_together_the_strongest_is_kept()
    {
        // A flash: in at frame 3, out at frame 4, then a real cut at frame 9.
        var scores = Scores(10, 0, 0, 0, 15, 25, 0, 0, 0, 0, 30);
        Assert.Equal([0.4, 0.9], scores.Changes(minGap: 0.5));
    }

    [Fact]
    public void Scores_follow_scdet()
    {
        byte[] black = new byte[16], white = [.. Enumerable.Repeat((byte)255, 16)];
        Assert.Equal(0, SceneDetector.MeanDifference(black, black));
        Assert.Equal(255 * 100.0 / 256, SceneDetector.MeanDifference(black, white), 9);
        // Steady motion: large difference every frame, but no jump.
        Assert.Equal(0.5, SceneDetector.Score(20, 19.5), 9);
        // A cut after a still picture.
        Assert.Equal(29.8, SceneDetector.Score(30, 0.2), 9);
    }

    [Fact]
    public void The_rate_is_the_videos_own_up_to_60_fps()
    {
        static MediaInfo With(double fps) => new("/v.mp4", "mov,mp4", 10, 0, 0, 0,
            new VideoStreamInfo(0, "h264", 1920, 1080, fps, "", false, "yuv420p", 0), [], []);
        Assert.Equal(29.97, SceneDetector.RateFor(With(29.97)));
        Assert.Equal(60, SceneDetector.RateFor(With(60)));
        Assert.Equal(30, SceneDetector.RateFor(With(1000)));
        Assert.Equal(30, SceneDetector.RateFor(With(0)));
    }
}

/// <summary>
/// A 12.5 s clip with hard cuts at 3, 6 and 9 s (steady motion in between) and a tone with pauses at
/// 2–4 s, 7–8.5 s and a short one at 10.5–11 s. Skipped when ffmpeg is not installed.
/// </summary>
public sealed class AnalysisIntegrationTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-analysis").FullName;
    private string Video => Path.Combine(_dir, "scenes.mp4");
    private bool _available;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (NativeTools.FindTool("ffmpeg") is null || NativeTools.FindTool("ffprobe") is null)
            return;
        const string Graph =
            "testsrc2=size=320x180:rate=30:duration=3[v0];smptehdbars=size=320x180:rate=30:duration=3[v1];" +
            "mandelbrot=size=320x180:rate=30,trim=duration=3[v2];testsrc=size=320x180:rate=30:duration=3[v3];" +
            "[v0][v1][v2][v3]concat=n=4:v=1:a=0,format=yuv420p[v];" +
            "sine=frequency=440:duration=2[a0];aevalsrc=0:d=2[a1];sine=frequency=440:duration=3[a2];aevalsrc=0:d=1.5[a3];" +
            "sine=frequency=440:duration=2[a4];aevalsrc=0:d=0.5[a5];sine=frequency=440:duration=1.5[a6];" +
            "[a0][a1][a2][a3][a4][a5][a6]concat=n=7:v=0:a=1[a]";
        await ToolProcess.RunAsync("ffmpeg",
        [
            "-v", "error", "-filter_complex", Graph, "-map", "[v]", "-map", "[a]",
            "-c:v", "libx264", "-preset", "veryfast", "-g", "30", "-bf", "2", "-c:a", "aac", "-y", Video,
        ], null, CancellationToken.None);
        _available = true;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Temp folder; the OS cleans it up.
        }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Scene_detection_finds_the_cuts_but_not_steady_motion()
    {
        Assert.SkipUnless(_available, "ffmpeg/ffprobe are not installed.");
        var info = await MediaProbe.ProbeAsync(Video, Ct);
        var scores = SceneDetector.Create(info);
        int chunks = 0;
        await SceneDetector.DetectAsync(info, scores, () => chunks++, Ct);

        Assert.True(scores.IsComplete);
        Assert.Equal(30, scores.Rate);
        Assert.InRange(scores.Filled, 359, 376);
        Assert.True(chunks >= 1);
        var changes = scores.Changes();
        Assert.Equal(3, changes.Count);
        Assert.Equal(3, changes[0], 0.05);
        Assert.Equal(6, changes[1], 0.05);
        Assert.Equal(9, changes[2], 0.05);

        // The scores are cached, so another sensitivity needs no second pass.
        var cache = new MediaCache(Path.Combine(_dir, "cache"));
        cache.SaveSceneScores(Video, scores);
        var cached = Assert.IsType<SceneScores>(cache.LoadSceneScores(Video));
        Assert.Equal(changes, cached.Changes());
        Assert.Equal(scores.Filled, cached.Filled);
    }

    [Fact]
    public async Task Silence_detection_finds_the_pauses_in_the_waveform()
    {
        Assert.SkipUnless(_available, "ffmpeg/ffprobe are not installed.");
        var info = await MediaProbe.ProbeAsync(Video, Ct);
        var wave = WaveformExtractor.Create(info);
        await WaveformExtractor.ExtractAsync(info, wave, cancellationToken: Ct);

        var result = SilenceDetector.Find(wave);

        Assert.Equal(2, result.Ranges.Count);
        Assert.Equal(2, result.Ranges[0].Start, 0.1);
        Assert.Equal(4, result.Ranges[0].End, 0.1);
        Assert.Equal(7, result.Ranges[1].Start, 0.1);
        Assert.Equal(8.5, result.Ranges[1].End, 0.1);
        Assert.Equal(3, SilenceDetector.Find(wave, minDuration: 0.3).Ranges.Count);
    }
}
