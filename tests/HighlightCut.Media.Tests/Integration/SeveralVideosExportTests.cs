using HighlightCut.Core.Model;
using HighlightCut.Media.Export;
using HighlightCut.Media.Previews;
using HighlightCut.Media.Probing;
using HighlightCut.Media.Tools;

namespace HighlightCut.Media.Tests.Integration;

/// <summary>
/// Generates plain-coloured videos to join, 4 s each with a keyframe every second: red and blue are alike (320×180, 30 fps,
/// H.264, two AAC tracks: a 440 Hz and an 880 Hz tone); blue-one-track has only the first track; green is 240×240 at 25 fps
/// with one track. Tests skip themselves when ffmpeg is not installed.
/// </summary>
public sealed class ColourVideosFixture : IAsyncLifetime
{
    public string Folder { get; } = Directory.CreateTempSubdirectory("highlightcut-join").FullName;
    public string Red => Path.Combine(Folder, "red.mp4");
    public string Blue => Path.Combine(Folder, "blue.mp4");
    public string BlueOneTrack => Path.Combine(Folder, "blue one track.mp4");
    public string Green => Path.Combine(Folder, "green.mp4");

    public bool IsAvailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (NativeTools.FindTool("ffmpeg") is null || NativeTools.FindTool("ffprobe") is null)
            return;
        await MakeAsync(Red, "red", "320x180", 30, tracks: 2);
        await MakeAsync(Blue, "blue", "320x180", 30, tracks: 2);
        await MakeAsync(BlueOneTrack, "blue", "320x180", 30, tracks: 1);
        await MakeAsync(Green, "green", "240x240", 25, tracks: 1);
        IsAvailable = true;
    }

    private static Task MakeAsync(string path, string colour, string size, int rate, int tracks)
    {
        var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", $"color=c={colour}:s={size}:r={rate}" };
        for (int t = 0; t < tracks; t++)
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency={440 * (t + 1)}:sample_rate=48000"]);
        args.AddRange(["-t", "4", "-map", "0:v"]);
        for (int t = 0; t < tracks; t++)
            args.AddRange(["-map", $"{t + 1}:a"]);
        args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-g", rate.ToString(CultureInfo.InvariantCulture), "-keyint_min",
            rate.ToString(CultureInfo.InvariantCulture), "-sc_threshold", "0", "-bf", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", path]);
        return ToolProcess.RunAsync("ffmpeg", args, null, CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Temp folder; the OS cleans it up.
        }
        return ValueTask.CompletedTask;
    }

    public void SkipIfUnavailable() => Assert.SkipUnless(IsAvailable, "ffmpeg/ffprobe are not installed.");

    public string NewOutputFolder() => Directory.CreateDirectory(Path.Combine(Folder, "out-" + Guid.NewGuid().ToString("N")[..8])).FullName;
}

/// <summary>Exports of projects with several videos, run with ffmpeg and checked at the joins.</summary>
public class SeveralVideosExportTests(ColourVideosFixture media) : IClassFixture<ColourVideosFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_compatible_videos_are_joined_losslessly()
    {
        media.SkipIfUnavailable();
        var (project, sources) = await Project(media.Red, media.Blue);
        string folder = media.NewOutputFolder();
        // What the comparison reads, as ffprobe gives it.
        var red = sources[1].Info;
        Assert.Equal(("30/1", "1/15360"), (red.Video!.FrameRateRational, red.Video.TimeBase));
        Assert.Equal(["mono", "mono"], red.Audio.Select(a => a.ChannelLayout));

        var plan = ExportPlanner.Plan(project, sources, Settings(folder));
        string output = Assert.Single(await ExportRunner.RunAsync(plan, cancellationToken: Ct));

        Assert.Equal([ExportStepKind.Cut, ExportStepKind.Cut, ExportStepKind.Concat], plan.Steps.Select(s => s.Kind));
        var result = await MediaProbe.ProbeAsync(output, Ct);
        Assert.Equal(4, result.Duration, 0.1);
        Assert.Equal("h264", result.Video?.Codec);
        Assert.Equal((320, 180), (result.Video!.Width, result.Video.Height));
        Assert.Equal(["aac", "aac"], result.Audio.Select(a => a.Codec));
        // Red until the join at 2 s, blue after it.
        Assert.Equal("red", await ColourAt(output, 1.9));
        Assert.Equal("blue", await ColourAt(output, 2.1));
        Assert.Equal([output], Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Videos_of_another_size_and_frame_rate_are_refused_for_lossless_and_re_encoded()
    {
        media.SkipIfUnavailable();
        var (project, sources) = await Project(media.Red, media.Green);
        string folder = media.NewOutputFolder();

        var refusal = Assert.Throws<InvalidOperationException>(() => ExportPlanner.Plan(project, sources, Settings(folder)));
        Assert.Contains("next to red.mp4, green.mp4 has 240×240 instead of 320×180, 25 fps instead of 30", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("no audio track 2", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(folder));

        var plan = ExportPlanner.Plan(project, sources, Settings(folder) with { Mode = CutMode.Reencode, Video = VideoEncoding.H264Fast });
        string output = Assert.Single(await ExportRunner.RunAsync(plan, cancellationToken: Ct));

        var result = await MediaProbe.ProbeAsync(output, Ct);
        Assert.Equal(4, result.Duration, 0.1);
        // The first video's size and frame rate; green is letterboxed into it, not stretched.
        Assert.Equal((320, 180, "30"), (result.Video!.Width, result.Video.Height, result.Video.FrameRateText));
        Assert.Equal("red", await ColourAt(output, 1.9));
        Assert.Equal("green", await ColourAt(output, 2.1));
        Assert.Equal("black", await ColourAt(output, 2.1, x: 10));
        Assert.Equal("green", await ColourAt(output, 2.1, x: 160));
        Assert.Equal(2, result.Audio.Length);
        Assert.All(result.Audio, a => Assert.Equal(48000, a.SampleRate));
    }

    [Fact]
    public async Task A_video_without_a_track_is_silent_on_it()
    {
        media.SkipIfUnavailable();
        var (project, sources) = await Project(media.Red, media.BlueOneTrack);
        string folder = media.NewOutputFolder();

        var refusal = Assert.Throws<InvalidOperationException>(() => ExportPlanner.Plan(project, sources, Settings(folder)));
        Assert.Contains("blue one track.mp4 has no audio track 2", refusal.Message, StringComparison.Ordinal);

        var plan = ExportPlanner.Plan(project, sources, Settings(folder) with { Mode = CutMode.Reencode, Video = VideoEncoding.H264Fast });
        string output = Assert.Single(await ExportRunner.RunAsync(plan, cancellationToken: Ct));

        var wave = await Waveform(output);
        Assert.Equal(2, wave.StreamCount);
        Assert.Equal(WaveformData.ToDisplay(0.125), wave.Peak(0, 0.5, 1.5), 1);
        Assert.Equal(WaveformData.ToDisplay(0.125), wave.Peak(0, 2.5, 3.5), 1);
        Assert.Equal(WaveformData.ToDisplay(0.125), wave.Peak(1, 0.5, 1.5), 1);
        Assert.Equal(0, wave.Peak(1, 2.5, 3.5), 2);
    }

    [Fact]
    public async Task Separate_files_cut_each_clip_from_its_own_video_losslessly()
    {
        media.SkipIfUnavailable();
        var (project, sources) = await Project(media.Red, media.Green);
        string folder = media.NewOutputFolder();

        var plan = ExportPlanner.Plan(project, sources, Settings(folder) with { Merge = false });
        var written = await ExportRunner.RunAsync(plan, cancellationToken: Ct);

        Assert.Equal(["sample-cut-01.mp4", "sample-cut-02.mp4"], written.Select(Path.GetFileName));
        Assert.Equal("red", await ColourAt(written[0], 1));
        var green = await MediaProbe.ProbeAsync(written[1], Ct);
        Assert.Equal((240, 240), (green.Video!.Width, green.Video.Height));
        Assert.Single(green.Audio);
    }

    [Theory]
    [InlineData(CutMode.Lossless)]
    [InlineData(CutMode.Reencode)]
    public async Task Each_video_plays_at_its_own_volume(CutMode mode)
    {
        media.SkipIfUnavailable();
        var (project, sources) = await Project(media.Red, media.Blue);
        string folder = media.NewOutputFolder();
        // Red's first track 12 dB down, blue's as it is; both second tracks 6 dB down, which a lossless join applies once.
        // (lavfi's sine is at 1/8 amplitude, −18 dB.)
        var red = sources[1].Info;
        var blue = sources[2].Info;
        var settings = Settings(folder) with
        {
            Mode = mode, Video = VideoEncoding.H264Fast,
            SourceAudio = new Dictionary<int, SourceAudioSettings>
            {
                [1] = new([.. red.Audio.Select(a => a.Index)], new Dictionary<int, double> { [red.Audio[0].Index] = -12, [red.Audio[1].Index] = -6 }),
                [2] = new([.. blue.Audio.Select(a => a.Index)], new Dictionary<int, double> { [blue.Audio[1].Index] = -6 }),
            },
        };

        string output = Assert.Single(await ExportRunner.RunAsync(ExportPlanner.Plan(project, sources, settings), cancellationToken: Ct));

        var wave = await Waveform(output);
        Assert.Equal(WaveformData.ToDisplay(0.125 * Math.Pow(10, -12 / 20.0)), wave.Peak(0, 0.5, 1.5), 1);
        Assert.Equal(WaveformData.ToDisplay(0.125), wave.Peak(0, 2.5, 3.5), 1);
        Assert.Equal(WaveformData.ToDisplay(0.125 * Math.Pow(10, -6 / 20.0)), wave.Peak(1, 0.5, 1.5), 1);
        Assert.Equal(WaveformData.ToDisplay(0.125 * Math.Pow(10, -6 / 20.0)), wave.Peak(1, 2.5, 3.5), 1);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    /// <summary>Two videos end to end, a 2 s clip from 1 s into each (on a keyframe, so lossless cuts are exact).</summary>
    private static async Task<(Project Project, Dictionary<int, ExportSource> Sources)> Project(string first, string second)
    {
        var a = await Analyse(first);
        var b = await Analyse(second);
        var project = new Project("sample", [], [new Clip(1, "First", 1, 3), new Clip(2, "Second", 1, 3, SourceId: 2)])
            .WithSources([a.Info.ToSourceMedia(), b.Info.ToSourceMedia() with { Id = 2 }]);
        return (project, new Dictionary<int, ExportSource> { [1] = a, [2] = b });
    }

    private static async Task<ExportSource> Analyse(string path)
    {
        var info = await MediaProbe.ProbeAsync(path, Ct);
        return new ExportSource(info, await KeyframeScanner.ScanAsync(info, cancellationToken: Ct));
    }

    private static ExportSettings Settings(string folder) => new() { OutputFolder = folder, BaseName = "sample" };

    private static async Task<WaveformData> Waveform(string path)
    {
        var info = await MediaProbe.ProbeAsync(path, Ct);
        var wave = WaveformExtractor.Create(info);
        await WaveformExtractor.ExtractAsync(info, wave, cancellationToken: Ct);
        return wave;
    }

    /// <summary>The colour of the frame at <paramref name="time"/>, around a point on its middle row (the centre by default).</summary>
    private async Task<string> ColourAt(string path, double time, int? x = null)
    {
        var info = await MediaProbe.ProbeAsync(path, Ct);
        int px = x ?? info.Video!.Width / 2, py = info.Video!.Height / 2;
        string raw = Path.Combine(media.Folder, Guid.NewGuid().ToString("N") + ".rgb");
        await ToolProcess.RunAsync("ffmpeg",
        [
            "-v", "error", "-ss", time.ToString(CultureInfo.InvariantCulture), "-i", path, "-frames:v", "1",
            "-vf", $"crop=4:4:{px - 2}:{py - 2},scale=1:1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-y", raw,
        ], null, Ct);
        byte[] rgb = await File.ReadAllBytesAsync(raw, Ct);
        File.Delete(raw);
        return (rgb[0], rgb[1], rgb[2]) switch
        {
            var (r, g, b) when r < 40 && g < 40 && b < 40 => "black",
            var (r, g, b) when r > 150 && g < 80 && b < 80 => "red",
            var (r, g, b) when b > 150 && r < 80 && g < 80 => "blue",
            var (r, g, b) when g > 90 && r < 80 && b < 80 => "green",
            var (r, g, b) => $"rgb({r},{g},{b})",
        };
    }
}
