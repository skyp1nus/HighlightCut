using System.Text.Json;
using HighlightCut.Core.Model;
using HighlightCut.Media.Analysis;
using HighlightCut.Media.Caching;
using HighlightCut.Media.Export;
using HighlightCut.Media.Previews;
using HighlightCut.Media.Probing;
using HighlightCut.Media.Tools;

namespace HighlightCut.Media.Tests.Integration;

/// <summary>Runs ffprobe/ffmpeg on generated media. Skipped when the tools are not installed.</summary>
public class MediaIntegrationTests(SampleMediaFixture media) : IClassFixture<SampleMediaFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Probe_reads_streams_and_titles()
    {
        media.SkipIfUnavailable();
        var info = await MediaProbe.ProbeAsync(media.Mp4, Ct);

        Assert.Equal(ContainerFamily.Mov, info.Family);
        Assert.Equal(10, info.Duration, 1);
        var v = Assert.IsType<VideoStreamInfo>(info.Video);
        Assert.Equal(("h264", 320, 180, 30.0), (v.Codec, v.Width, v.Height, v.FrameRate));
        Assert.True(v.HasBFrames);
        Assert.Equal(["Mic", "Music"], info.Audio.Select(a => a.Label));
        Assert.Equal([0, 1], info.Audio.Select(a => a.Position));
        Assert.Equal("sample clip.mp4 · 320×180 · 30 fps", info.Summary);
    }

    [Fact]
    public async Task Probe_recognises_matroska()
    {
        media.SkipIfUnavailable();
        var info = await MediaProbe.ProbeAsync(media.Mkv, Ct);
        Assert.Equal(ContainerFamily.Matroska, info.Family);
        Assert.Equal(["Mic", "Music"], info.Audio.Select(a => a.Label));
    }

    [Fact]
    public async Task Probe_of_a_non_media_file_fails_with_ffprobes_message()
    {
        media.SkipIfUnavailable();
        string path = Path.Combine(media.Folder, "notes.txt");
        await File.WriteAllTextAsync(path, "not a video", Ct);
        var ex = await Assert.ThrowsAsync<MediaToolException>(() => MediaProbe.ProbeAsync(path, Ct));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    public async Task Keyframes_are_found_every_second(string kind)
    {
        media.SkipIfUnavailable();
        var info = await MediaProbe.ProbeAsync(kind == "mp4" ? media.Mp4 : media.Mkv, Ct);
        var reports = new List<double>();
        double[] keyframes = await KeyframeScanner.ScanAsync(info, new SyncProgress<double>(reports.Add), Ct);

        Assert.Equal(10, keyframes.Length);
        Assert.InRange(keyframes[0], 0, 0.05);
        for (int i = 0; i < keyframes.Length; i++)
            Assert.Equal(i, keyframes[i] - keyframes[0], 2);
        Assert.Equal(1, reports[^1]);
    }

    [Fact]
    public async Task An_mp4_without_B_frames_has_its_keyframes_read_alone()
    {
        media.SkipIfUnavailable();
        string path = Path.Combine(media.NewOutputFolder(), "no b-frames.mp4");
        await ToolProcess.RunAsync("ffmpeg",
        [
            "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30000/1001", "-t", "10", "-c:v", "libx264", "-preset", "veryfast",
            "-g", "45", "-keyint_min", "45", "-sc_threshold", "0", "-bf", "0", "-pix_fmt", "yuv420p", "-y", path,
        ], null, Ct);
        var info = await MediaProbe.ProbeAsync(path, Ct);
        Assert.True(KeyframeScanner.CanSkipToKeyframes(info));
        var reports = new List<double>();

        double[]? keyframes = await KeyframeScanner.ReadKeyframesOnlyAsync(info, new SyncProgress<double>(reports.Add), Ct);

        Assert.NotNull(keyframes);
        Assert.Equal(await KeyframeScanner.ReadThroughAsync(info, null, Ct), keyframes);
        Assert.Equal([0, 1.5015, 3.003, 4.5045, 6.006, 7.5075, 9.009], keyframes);
        Assert.Equal(1, reports[^1]);
        Assert.Equal(keyframes, await KeyframeScanner.ScanAsync(info, cancellationToken: Ct));
    }

    [Fact]
    public async Task With_B_frames_the_keyframes_are_read_through()
    {
        media.SkipIfUnavailable();
        var info = await MediaProbe.ProbeAsync(media.Mp4, Ct);
        Assert.False(KeyframeScanner.CanSkipToKeyframes(info));

        // Had the probe missed the B-frames, reading the keyframes alone would give up on seeing them.
        Assert.Null(await KeyframeScanner.ReadKeyframesOnlyAsync(info, null, Ct));
    }

    [Fact]
    public async Task Waveform_has_the_level_of_each_stream()
    {
        media.SkipIfUnavailable();
        var info = await MediaProbe.ProbeAsync(media.Mp4, Ct);
        var data = WaveformExtractor.Create(info);
        await WaveformExtractor.ExtractAsync(info, data, cancellationToken: Ct);

        Assert.True(data.IsComplete);
        Assert.InRange(data.Filled, 990, data.Capacity);
        // lavfi's sine is at 1/8 amplitude (-18 dB).
        Assert.Equal(WaveformData.ToDisplay(0.125), data.Peak(0, 2, 3), 1);
        Assert.Equal(WaveformData.ToDisplay(0.125), data.Peak(1, 2, 3), 1);
    }

    [Fact]
    public async Task A_long_file_decoded_in_stretches_gives_the_same_waveform_as_one_pass()
    {
        media.SkipIfUnavailable();
        string path = Path.Combine(media.NewOutputFolder(), "long.m4a");
        // 75 s of a tone whose loudness rises and falls every 10 s, and a stereo second stream at a steady level.
        await ToolProcess.RunAsync("ffmpeg",
        [
            "-v", "error", "-f", "lavfi", "-i", "aevalsrc=0.5*sin(2*PI*440*t)*(0.55+0.45*sin(2*PI*0.1*t)):s=48000:d=75",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100:duration=75", "-map", "0:a", "-map", "1:a",
            "-ac:1", "2", "-c:a", "aac", "-y", path,
        ], null, Ct);
        var info = await MediaProbe.ProbeAsync(path, Ct);
        var whole = WaveformExtractor.Create(info);
        var parts = WaveformExtractor.Create(info);
        var chunks = 0;

        await WaveformExtractor.ExtractAsync(info, whole, 1, null, Ct);
        await WaveformExtractor.ExtractAsync(info, parts, 3, () => Interlocked.Increment(ref chunks), Ct);

        Assert.True(parts.IsComplete);
        Assert.Equal(whole.Filled, parts.Filled);
        Assert.Equal(whole.Decoded, parts.Decoded);
        Assert.True(chunks >= 3);
        int firstStretch = (int)Math.Ceiling(whole.Capacity / 3.0);
        for (int s = 0; s < 2; s++)
        {
            // The first stretch starts at 0 like the single pass: the same peaks.
            for (int b = 0; b < firstStretch; b++)
                Assert.True(Math.Abs(whole[s, b] - parts[s, b]) < 0.02, $"stream {s}, bucket {b}: {whole[s, b]} vs {parts[s, b]}");
            // The others start with a seek, which may land a few ms off (ffmpeg 9: 14 ms): the peaks match within 30 ms.
            for (int b = firstStretch; b < whole.Filled; b++)
            {
                Assert.True(parts[s, b] <= Loudest(whole, s, b) + 0.02, $"stream {s}, bucket {b}: {parts[s, b]} louder than one pass");
                Assert.True(whole[s, b] <= Loudest(parts, s, b) + 0.02, $"stream {s}, bucket {b}: {whole[s, b]} missing");
            }
        }

        static float Loudest(WaveformData data, int stream, int bucket) =>
            Enumerable.Range(Math.Max(0, bucket - 3), 7).Where(b => b < data.Capacity).Max(b => data[stream, b]);
    }

    [Fact]
    public async Task Thumbnails_are_decoded_at_the_requested_interval()
    {
        media.SkipIfUnavailable();
        var info = await MediaProbe.ProbeAsync(media.Mp4, Ct);
        var frames = new List<ThumbnailFrame>();
        await ThumbnailExtractor.ExtractAsync(info, frames.Add, 90, 2, Ct);

        Assert.Equal(5, frames.Count);
        Assert.Equal([0.0, 2, 4, 6, 8], frames.Select(f => f.Time));
        Assert.All(frames, f => Assert.Equal((160, 90, 160 * 90 * 4), (f.Width, f.Height, f.Bgra.Length)));
        Assert.Contains(frames[0].Bgra, b => b > 32);
    }

    [Fact]
    public async Task Cache_round_trips_analysis_results()
    {
        media.SkipIfUnavailable();
        var info = await MediaProbe.ProbeAsync(media.Mp4, Ct);
        var cache = new MediaCache(Path.Combine(media.Folder, "cache"));
        Assert.Null(cache.LoadKeyframes(info.Path));
        Assert.Null(cache.LoadWaveform(info.Path));
        Assert.Null(cache.LoadThumbnails(info.Path, 90));

        double[] keyframes = await KeyframeScanner.ScanAsync(info, cancellationToken: Ct);
        var waveform = WaveformExtractor.Create(info);
        await WaveformExtractor.ExtractAsync(info, waveform, cancellationToken: Ct);
        var frames = new List<ThumbnailFrame>();
        await ThumbnailExtractor.ExtractAsync(info, frames.Add, 90, 2, Ct);

        cache.SaveKeyframes(info.Path, keyframes);
        cache.SaveWaveform(info.Path, waveform);
        cache.SaveThumbnails(info.Path, frames);

        Assert.Equal(keyframes, cache.LoadKeyframes(info.Path));
        var loadedWave = Assert.IsType<WaveformData>(cache.LoadWaveform(info.Path));
        Assert.Equal(waveform.Filled, loadedWave.Filled);
        Assert.Equal(waveform.Peak(1, 0, 10), loadedWave.Peak(1, 0, 10));
        var loadedFrames = Assert.IsType<IReadOnlyList<ThumbnailFrame>>(cache.LoadThumbnails(info.Path, 90), exactMatch: false);
        Assert.Equal(frames.Select(f => f.Time), loadedFrames.Select(f => f.Time));
        for (int i = 0; i < frames.Count; i++)
            Assert.InRange(MeanDifference(frames[i].Bgra, loadedFrames[i].Bgra), 0, 8);
        Assert.Null(cache.LoadThumbnails(info.Path, 60));
    }

    [Fact]
    public async Task Cache_misses_after_the_file_changes()
    {
        media.SkipIfUnavailable();
        string copy = Path.Combine(media.Folder, "changing.mp4");
        File.Copy(media.Mp4, copy, overwrite: true);
        var cache = new MediaCache(Path.Combine(media.Folder, "cache2"));
        cache.SaveKeyframes(copy, [0, 1]);
        Assert.NotNull(cache.LoadKeyframes(copy));

        await File.AppendAllTextAsync(copy, "x", Ct);
        Assert.Null(cache.LoadKeyframes(copy));
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    public async Task Lossless_merge_keeps_every_stream_and_adds_chapters(string kind)
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(kind == "mp4" ? media.Mp4 : media.Mkv);
        string folder = media.NewOutputFolder();
        var settings = Settings(folder) with { Container = kind == "mp4" ? OutputContainer.Mp4 : OutputContainer.Mkv };
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, settings);

        var reports = new List<ExportProgress>();
        var written = await ExportRunner.RunAsync(plan, new SyncProgress<ExportProgress>(reports.Add), Ct);

        string output = Assert.Single(written);
        Assert.Equal($"sample-cut.{kind}", Path.GetFileName(output));
        Assert.Equal([output], Directory.GetFiles(folder));

        var result = await MediaProbe.ProbeAsync(output, Ct);
        AssertLosslessLength(plan.OutputDuration, result.Duration, plan.Clips.Count);
        Assert.Equal("h264", result.Video?.Codec);
        Assert.Equal(["Mic", "Music"], result.Audio.Select(a => a.Label));

        // Chapters follow the real length of each cut, so the second starts where its clip does.
        var chapters = await Chapters(output);
        Assert.Equal(["Intro", "Demo — import"], chapters.Select(c => c.Title));
        Assert.Equal(0, chapters[0].Start, 2);
        AssertLosslessLength(plan.Clips[0].OutputDuration, chapters[1].Start, 1);

        Assert.Equal(1, reports[^1].Overall, 9);
        Assert.True(reports.Select(r => r.Overall).Zip(reports.Skip(1).Select(r => r.Overall)).All(p => p.Second >= p.First - 1e-9),
            "Overall progress must not go backwards.");
    }

    [Fact]
    public async Task Separate_files_have_one_clip_each()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, Settings(folder) with { Merge = false });

        var written = await ExportRunner.RunAsync(plan, cancellationToken: Ct);

        Assert.Equal(["sample-cut-01.mp4", "sample-cut-02.mp4"], written.Select(Path.GetFileName));
        AssertLosslessLength(2.2, (await MediaProbe.ProbeAsync(written[0], Ct)).Duration, 1);
        AssertLosslessLength(2.0, (await MediaProbe.ProbeAsync(written[1], Ct)).Duration, 1);
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public async Task Reencoded_merge_is_frame_accurate()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        var settings = Settings(folder) with { Mode = CutMode.Reencode, Video = VideoEncoding.H264Fast };
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, settings);

        string output = Assert.Single(await ExportRunner.RunAsync(plan, cancellationToken: Ct));

        var result = await MediaProbe.ProbeAsync(output, Ct);
        Assert.Equal(3.7, result.Duration, 0.1);
        Assert.Equal(["aac", "aac"], result.Audio.Select(a => a.Codec));
        Assert.Equal(2, (await Chapters(output)).Count);
        Assert.Equal([output], Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Reencoded_clips_with_copied_audio_have_no_lead_in()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        var settings = Settings(folder) with
        {
            Mode = CutMode.Reencode, Merge = false, Container = OutputContainer.Mkv, Video = VideoEncoding.H264Fast,
        };
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, settings);

        var written = await ExportRunner.RunAsync(plan, cancellationToken: Ct);

        var first = await MediaProbe.ProbeAsync(written[0], Ct);
        Assert.Equal(1.7, first.Duration, 0.05);
        Assert.Equal(["aac", "aac"], first.Audio.Select(a => a.Codec));
    }

    [Theory]
    [InlineData("mp4", CutMode.Lossless, true)]
    [InlineData("mkv", CutMode.Lossless, true)]
    [InlineData("mp4", CutMode.Lossless, false)]
    [InlineData("mp4", CutMode.Reencode, true)]
    public async Task A_track_volume_changes_only_that_track(string kind, CutMode mode, bool merge)
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(kind == "mp4" ? media.Mp4 : media.Mkv);
        string folder = media.NewOutputFolder();
        var settings = Settings(folder) with
        {
            Mode = mode, Merge = merge, Video = VideoEncoding.H264Fast,
            Container = kind == "mp4" ? OutputContainer.Mp4 : OutputContainer.Mkv,
            AudioGainsDb = new Dictionary<int, double> { [info.Audio[1].Index] = -12 },
        };
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, settings);

        string output = (await ExportRunner.RunAsync(plan, cancellationToken: Ct))[0];

        var result = await MediaProbe.ProbeAsync(output, Ct);
        Assert.Equal(2, result.Audio.Length);
        Assert.Equal("h264", result.Video?.Codec);
        var wave = WaveformExtractor.Create(result);
        await WaveformExtractor.ExtractAsync(result, wave, cancellationToken: Ct);
        // lavfi's sine is at −18 dB; Music comes down 12 dB, Mic stays. (The level, not the loudest peak: a copied
        // track can click where the pieces of a lossless merge join.)
        Assert.Equal(-18, AudioLevels.Measure(wave, 0)!.Value.LevelDb, 0.5);
        Assert.Equal(-30, AudioLevels.Measure(wave, 1)!.Value.LevelDb, 0.5);
    }

    [Fact]
    public async Task Muted_tracks_are_left_out()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        var settings = Settings(folder) with { KeepAllTracks = false, AudioStreamIndexes = [info.Audio[1].Index] };
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, settings);

        string output = Assert.Single(await ExportRunner.RunAsync(plan, cancellationToken: Ct));

        Assert.Equal(["Music"], (await MediaProbe.ProbeAsync(output, Ct)).Audio.Select(a => a.Label));
    }

    [Fact]
    public async Task Cancelling_leaves_no_files_behind()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, Settings(folder));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        // Cancel when the second cut starts: the first temp file and the lists already exist.
        var progress = new SyncProgress<ExportProgress>(p =>
        {
            if (p.StepIndex == 1)
                cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportRunner.RunAsync(plan, progress, cts.Token));

        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Overwrite_replaces_the_file_and_a_cancelled_one_keeps_it()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        string output = Path.Combine(folder, "sample-cut.mp4");
        await File.WriteAllTextAsync(output, "old", Ct);
        var settings = Settings(folder) with { Overwrite = true };

        // Cancelled on the last step, the one that writes the file: the old file stays as it was.
        var cancelled = ExportPlanner.Plan(Project(info), info, keyframes, settings);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new SyncProgress<ExportProgress>(p =>
        {
            if (p.StepIndex == cancelled.Steps.Count - 1)
                cts.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportRunner.RunAsync(cancelled, progress, cts.Token));
        Assert.Equal("old", await File.ReadAllTextAsync(output, Ct));
        Assert.Equal([output], Directory.GetFiles(folder));

        var plan = ExportPlanner.Plan(Project(info), info, keyframes, settings);
        var written = await ExportRunner.RunAsync(plan, cancellationToken: Ct);

        Assert.Equal([output], written);
        Assert.Equal([output], Directory.GetFiles(folder));
        AssertLosslessLength(plan.OutputDuration, (await MediaProbe.ProbeAsync(output, Ct)).Duration, plan.Clips.Count);
    }

    [Fact]
    public async Task A_gpu_encoder_that_fails_hands_over_to_the_cpu()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        var missing = new GpuEncoder("Missing", "no_such_encoder");
        var plan = ExportPlanner.Plan(Project(info), info, keyframes,
            Settings(folder) with { Mode = CutMode.Reencode, Merge = false, Video = VideoEncoding.H264Fast, GpuEncoder = missing });

        var written = await ExportRunner.RunAsync(plan, cancellationToken: Ct);

        Assert.Equal(plan.Outputs, written);
        Assert.Equal(2, Directory.GetFiles(folder).Length);
        Assert.Equal(1.7, (await MediaProbe.ProbeAsync(written[0], Ct)).Duration, 0.1);
    }

    [Fact]
    public async Task A_gpu_encoder_is_found_only_if_it_encodes()
    {
        media.SkipIfUnavailable();

        var found = await GpuEncoderProbe.DetectAsync(cancellationToken: Ct);

        // No GPU on CI: none. On a computer with one, what was found really encodes.
        if (found is not null)
            await ToolProcess.RunAsync("ffmpeg", GpuEncoderProbe.TestArguments(found.Encoder, VideoEncoding.H264Quality), null, Ct);
    }

    [Fact]
    public async Task Cancelling_before_the_first_step_removes_the_chapters_file()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        // A re-encoded merge writes its chapters file before ffmpeg runs.
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, Settings(folder) with { Mode = CutMode.Reencode });
        Assert.NotNull(plan.ChaptersPath);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportRunner.RunAsync(plan, cancellationToken: cts.Token));

        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Ffmpeg_errors_are_reported_and_cleaned_up()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        // A stream index that does not exist makes ffmpeg fail.
        var broken = info with { Audio = [info.Audio[0] with { Index = 9 }] };
        var plan = ExportPlanner.Plan(Project(info), broken, keyframes, Settings(folder));

        var ex = await Assert.ThrowsAsync<MediaToolException>(() => ExportRunner.RunAsync(plan, cancellationToken: Ct));

        // The explaining line, not ffmpeg's generic "Error opening output files" (its wording varies by version:
        // 6.x names the map, "Stream map '0:9' …"; 9.x leaves it out).
        Assert.Contains("matches no streams", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(folder));
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static async Task<(MediaInfo Info, double[] Keyframes)> Analyse(string path)
    {
        var info = await MediaProbe.ProbeAsync(path, Ct);
        return (info, await KeyframeScanner.ScanAsync(info, cancellationToken: Ct));
    }

    /// <summary>
    /// A stream copy never loses content, but ffmpeg ends it by decode time, so with B-frames each clip
    /// comes out up to a few frames longer than planned.
    /// </summary>
    private static void AssertLosslessLength(double planned, double actual, int clips) =>
        Assert.InRange(actual, planned - 0.03, planned + 0.25 * clips);

    private static Project Project(MediaInfo info) => new("sample", info.ToSourceMedia(),
    [
        new Clip(1, "Intro", 1.5, 3.2),
        new Clip(2, "Demo — import", 5, 7),
        new Clip(3, "Skipped", 8, 9, IsIncluded: false),
    ]);

    private static ExportSettings Settings(string folder) => new() { OutputFolder = folder, BaseName = "sample" };

    private static async Task<List<(string Title, double Start)>> Chapters(string path)
    {
        string json = await ToolProcess.ReadAllTextAsync("ffprobe", ["-v", "error", "-show_chapters", "-of", "json", "-i", path], Ct);
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.GetProperty("chapters").EnumerateArray().Select(c => (
            c.GetProperty("tags").GetProperty("title").GetString()!,
            double.Parse(c.GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture)))];
    }

    private static double MeanDifference(byte[] a, byte[] b) => a.Zip(b).Average(p => Math.Abs(p.First - p.Second));

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly Lock _lock = new();

        public void Report(T value)
        {
            lock (_lock)
                report(value);
        }
    }
}
