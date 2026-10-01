using HighlightCut.Core.Model;
using HighlightCut.Media.Export;
using HighlightCut.Media.Probing;
using static HighlightCut.Media.Tests.Export.ExportSample;

namespace HighlightCut.Media.Tests.Export;

/// <summary>
/// Plans for projects with several videos: the sample (video 1, 10 s, two audio tracks and a subtitle), then part2.mp4
/// (video 2, made the same way) or other.mp4 (video 2, 720p at 25 fps in HEVC with one audio track).
/// </summary>
public class ExportSeveralVideosTests
{
    private static readonly string Part2Path = Path.Combine(InputFolder, "part2.mp4");

    private static readonly MediaInfo Part2 = Info with { Path = Part2Path };

    private static readonly MediaInfo Other = new(
        Path.Combine(InputFolder, "other.mp4"), "mov,mp4,m4a,3gp,3g2,mj2", 10, 0, 4_000_000, 5_000_000,
        new VideoStreamInfo(0, "hevc", 1280, 720, 25, "25", HasBFrames: false, "yuv420p", 0),
        [new AudioStreamInfo(1, 0, "aac", 1, 44100, "Room", null)],
        []);

    /// <summary>Video 2's keyframes are half a second off video 1's, so a cut shows which ones it used.</summary>
    private static readonly double[] Part2Keyframes = [0.5, 1.5, 2.5, 3.5, 4.5, 5.5, 6.5, 7.5, 8.5, 9.5];

    private static Project TwoVideos(MediaInfo second, params Clip[] clips) =>
        new Project("demo", [], [.. clips]).WithSources([Info.ToSourceMedia(), second.ToSourceMedia() with { Id = 2 }]);

    /// <summary>Intro from video 1, Talk from video 2, then Outro from video 1 again.</summary>
    private static Project Mixed(MediaInfo second) => TwoVideos(second,
        new Clip(1, "Intro", 1.5, 3.2), new Clip(2, "Talk", 2, 4, SourceId: 2), new Clip(3, "Outro", 5, 7));

    private static Dictionary<int, ExportSource> Sources(MediaInfo second) =>
        new() { [1] = new ExportSource(Info, Keyframes), [2] = new ExportSource(second, Part2Keyframes) };

    private static ExportPlan PlanOf(Project project, ExportSettings settings, MediaInfo? second = null) =>
        ExportPlanner.Plan(project, Sources(second ?? Part2), settings, _ => false, tempId: "t1");

    /// <summary>A step's ffmpeg arguments with the folders written alike on every system.</summary>
    private static string Args(ExportPlan plan, int step) => FfmpegCommands.ForStep(plan, plan.Steps[step]).Arguments
        .Replace(InputFolder, "{in}", StringComparison.Ordinal).Replace(OutputFolder, "{out}", StringComparison.Ordinal).Replace('\\', '/');

    // ---- One video: as before ------------------------------------------------------------

    [Fact]
    public void A_project_with_one_video_is_planned_exactly_as_before()
    {
        // The same commands, byte for byte, as before projects could have several videos.
        var gains = Settings() with { AudioGainsDb = new Dictionary<int, double> { [2] = -6 } };
        var lossless = Plan(gains);
        Assert.Null(lossless.Videos);
        Assert.Null(lossless.Layout);
        Assert.Equal("-ss 1.001000 -i \"{in}/My clip's.mp4\" -t 2.199000 -map 0:0 -map 0:1 -map 0:2 -map 0:3 -c copy -avoid_negative_ts make_zero " +
                     "-map_metadata 0 -ignore_unknown -f mp4 \"{out}/.highlightcut-tmp-t1-001.mp4\" -y", Args(lossless, 0));
        Assert.Equal("-f concat -safe 0 -i \"{out}/.highlightcut-tmp-t1.ffconcat\" -f ffmetadata -i \"{out}/.highlightcut-tmp-t1.ffmeta\" " +
                     "-map 0 -c copy -map_metadata 0 -filter:a:1 volume=-6dB -c:a:1 aac -b:a:1 192k -map_chapters 1 -movflags +faststart " +
                     "-f mp4 \"{out}/demo-cut.mp4\" -y", Args(lossless, 2));

        var reencoded = Plan(gains with { Mode = CutMode.Reencode });
        Assert.Equal("-ss 1.500000 -t 1.700000 -i \"{in}/My clip's.mp4\" -ss 5.000000 -t 2.000000 -i \"{in}/My clip's.mp4\" " +
                     "-f ffmetadata -i \"{out}/.highlightcut-tmp-t1.ffmeta\" " +
                     "-filter_complex \"[0:v:0][0:a:0][0:a:1][1:v:0][1:a:0][1:a:1]concat=n=2:v=1:a=2[v][a0][c1];[c1]volume=-6dB[a1]\" " +
                     "-map \"[v]\" -map \"[a0]\" -map \"[a1]\" -c:v libx264 -preset medium -crf 18 -c:a aac -b:a 192k -map_chapters 2 " +
                     "-map_metadata 0 -movflags +faststart -f mp4 \"{out}/demo-cut.mp4\" -y", Args(reencoded, 0));

        var separate = Plan(Settings(merge: false) with { KeepAllTracks = false, AudioStreamIndexes = [2] });
        Assert.Equal("-ss 1.001000 -i \"{in}/My clip's.mp4\" -t 2.199000 -map 0:0 -map 0:2 -c copy -avoid_negative_ts make_zero -map_metadata 0 " +
                     "-ignore_unknown -movflags +faststart -f mp4 \"{out}/demo-cut-01.mp4\" -y", Args(separate, 0));
    }

    [Fact]
    public void Clips_of_one_video_in_a_project_with_several_export_like_one_video_with_that_videos_tracks()
    {
        var project = TwoVideos(Part2, new Clip(1, "Talk", 2, 4, SourceId: 2), new Clip(2, "Skipped", 1, 2, IsIncluded: false));
        var settings = Settings(merge: false) with
        {
            KeepAllTracks = false,
            SourceAudio = new Dictionary<int, SourceAudioSettings> { [2] = new([1], new Dictionary<int, double> { [1] = -3 }) },
        };

        var plan = PlanOf(project, settings);

        Assert.Equal(Part2Path, plan.Source.Path);
        Assert.Null(plan.Layout);
        Assert.Contains("-i \"{in}/part2.mp4\" -t 2.499000 -map 0:0 -map 0:1 -c copy -filter:a:0 volume=-3dB", Args(plan, 0), StringComparison.Ordinal);
    }

    // ---- Lossless -----------------------------------------------------------------------

    [Fact]
    public void Each_clip_is_cut_from_its_own_video_on_its_keyframes()
    {
        var plan = PlanOf(Mixed(Part2), Settings());

        Assert.Equal([ExportStepKind.Cut, ExportStepKind.Cut, ExportStepKind.Cut, ExportStepKind.Concat], plan.Steps.Select(s => s.Kind));
        Assert.Equal([1, 2, 1], plan.Clips.Select(c => c.SourceId));
        Assert.Equal([1.0, 1.5, 5.0], plan.Clips.Select(c => c.OutputStart));
        Assert.Equal(Part2Path, plan.InfoOf(plan.Clips[1]).Path);
        Assert.Contains("-i \"{in}/My clip's.mp4\"", Args(plan, 0), StringComparison.Ordinal);
        Assert.Contains("-ss 1.501000 -i \"{in}/part2.mp4\" -t 2.499000 -map 0:0 -map 0:1 -map 0:2 -c copy", Args(plan, 1), StringComparison.Ordinal);
        // Subtitles are not joined across videos.
        Assert.DoesNotContain("0:3", Args(plan, 1), StringComparison.Ordinal);
        Assert.Equal(new Dictionary<int, MediaInfo> { [1] = Info, [2] = Part2 }, plan.Videos);
    }

    [Fact]
    public void Clips_meeting_at_the_join_between_two_videos_are_cut_apart()
    {
        // Intro and More touch in video 1 and are cut as one; More ends video 1 where Next starts video 2, but those are two files.
        var project = TwoVideos(Part2, new Clip(1, "Intro", 6, 8), new Clip(2, "More", 8, 10), new Clip(3, "Next", 0, 2, SourceId: 2));

        var plan = PlanOf(project, Settings());

        Assert.Equal([(1, 6.0, 10.0), (2, 0.0, 2.0)], plan.Clips.Select(c => (c.SourceId, c.Start, c.End)));
    }

    [Fact]
    public void Videos_that_differ_are_refused_for_lossless_with_what_differs()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PlanOf(Mixed(Other), Settings(), Other));

        Assert.Equal("Lossless can’t join these videos into one file: next to My clip's.mp4, other.mp4 has hevc video instead of h264, " +
                     "1280×720 instead of 1920×1080, 25 fps instead of 30, audio track 1 in aac 44.1 kHz mono instead of aac 48 kHz stereo " +
                     "and no audio track 2. Re-encode to join them, or export separate files.", ex.Message);
        Assert.Equal(ex.Message, ExportPlanner.LosslessMergeProblem(Mixed(Other), new Dictionary<int, MediaInfo> { [1] = Info, [2] = Other },
            Settings()) + " " + ExportPlanner.LosslessAdvice);
        // Separate files and re-encoding are fine.
        Assert.Equal(3, PlanOf(Mixed(Other), Settings(merge: false), Other).Outputs.Count);
        Assert.Single(PlanOf(Mixed(Other), Settings(CutMode.Reencode), Other).Steps);
    }

    [Fact]
    public void Videos_that_match_have_no_lossless_problem_and_unused_ones_are_not_judged()
    {
        Assert.Null(ExportPlanner.LosslessMergeProblem(Mixed(Part2), new Dictionary<int, MediaInfo> { [1] = Info, [2] = Part2 }, Settings()));
        var onlyFirst = TwoVideos(Other, new Clip(1, "Intro", 1, 2), new Clip(2, "Talk", 2, 4, SourceId: 2, IsIncluded: false));
        Assert.Null(ExportPlanner.LosslessMergeProblem(onlyFirst, new Dictionary<int, MediaInfo> { [1] = Info, [2] = Other }, Settings()));
    }

    [Fact]
    public void A_muted_track_that_is_left_out_does_not_count_against_lossless()
    {
        // Only unmuted tracks: track 2 is muted in video 1 and missing in video 2, so it is not exported at all.
        var oneTrack = Part2 with { Audio = [Part2.Audio[0]] };
        var settings = Settings() with
        {
            KeepAllTracks = false,
            SourceAudio = new Dictionary<int, SourceAudioSettings>
            {
                [1] = new([1], new Dictionary<int, double>()),
                [2] = new([1], new Dictionary<int, double>()),
            },
        };
        Assert.Contains("no audio track 2", Assert.Throws<InvalidOperationException>(() => PlanOf(Mixed(oneTrack), Settings(), oneTrack)).Message,
            StringComparison.Ordinal);

        var plan = PlanOf(Mixed(oneTrack), settings, oneTrack);

        Assert.Equal([0], plan.Layout!.Lanes.Select(l => l.Position));
        Assert.Contains("-map 0:0 -map 0:1 -c copy -avoid_negative_ts", Args(plan, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void A_lossless_join_changes_a_volume_all_videos_share_once_and_differing_ones_per_piece()
    {
        // Track 2 is 6 dB down in both videos; track 1 is 12 dB down in video 2 only.
        var settings = Settings() with
        {
            SourceAudio = new Dictionary<int, SourceAudioSettings>
            {
                [1] = new([1, 2], new Dictionary<int, double> { [2] = -6 }),
                [2] = new([1, 2], new Dictionary<int, double> { [1] = -12, [2] = -6 }),
            },
        };

        var plan = PlanOf(Mixed(Part2), settings);

        Assert.Contains("-c copy -filter:a:0 volume=0dB -c:a:0 aac -b:a:0 192k -avoid_negative_ts", Args(plan, 0), StringComparison.Ordinal);
        Assert.Contains("-c copy -filter:a:0 volume=-12dB -c:a:0 aac -b:a:0 192k -avoid_negative_ts", Args(plan, 1), StringComparison.Ordinal);
        Assert.DoesNotContain("a:1", Args(plan, 1), StringComparison.Ordinal);
        Assert.Contains("-map 0 -c copy -map_metadata 0 -filter:a:1 volume=-6dB -c:a:1 aac -b:a:1 192k -map_chapters 1", Args(plan, 3),
            StringComparison.Ordinal);
        Assert.DoesNotContain("a:0", Args(plan, 3), StringComparison.Ordinal);
    }

    [Fact]
    public void An_export_never_overwrites_any_of_its_videos()
    {
        var second = Part2 with { Path = Out("demo-cut.mp4") };
        var ex = Assert.Throws<InvalidOperationException>(() => PlanOf(Mixed(second), Settings(), second));
        Assert.Equal("The export would overwrite the source file.", ex.Message);
    }

    [Fact]
    public void A_video_that_is_not_open_cannot_be_exported()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ExportPlanner.Plan(Mixed(Part2), new Dictionary<int, ExportSource> { [1] = new(Info, Keyframes) }, Settings()));
        Assert.Equal("part2.mp4 is not open, so it cannot be exported.", ex.Message);
    }

    // ---- Re-encoding --------------------------------------------------------------------

    [Fact]
    public void A_re_encoded_join_brings_every_video_to_the_first_ones_picture_and_tracks()
    {
        var settings = Settings(CutMode.Reencode) with
        {
            SourceAudio = new Dictionary<int, SourceAudioSettings> { [2] = new([1], new Dictionary<int, double> { [1] = -6 }) },
        };

        var plan = PlanOf(TwoVideos(Other, new Clip(1, "Intro", 1.5, 3.2), new Clip(2, "Talk", 2, 4, SourceId: 2)), settings, Other);

        var step = Assert.Single(plan.Steps);
        Assert.Equal(ExportStepKind.EncodeMerged, step.Kind);
        var layout = plan.Layout!;
        Assert.Equal((1920, 1080, "30"), (layout.Width, layout.Height, layout.FrameRate));
        Assert.Equal([(0, 48000, 2), (1, 48000, 2)], layout.Lanes.Select(l => (l.Position, l.SampleRate, l.Channels)));
        string fit = "scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=1920:1080:(ow-iw)/2:(oh-ih)/2:black,setsar=1," +
                     "fps=30,format=yuv420p";
        string stereo = "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo";
        Assert.Equal(
            "-ss 1.500000 -t 1.700000 -i \"{in}/My clip's.mp4\" -ss 2.000000 -t 2.000000 -i \"{in}/other.mp4\" " +
            "-f ffmetadata -i \"{out}/.highlightcut-tmp-t1.ffmeta\" " +
            $"-filter_complex \"[0:v:0]{fit}[v0];[0:a:0]{stereo}[a0x0];[0:a:1]{stereo}[a0x1];" +
            $"[1:v:0]{fit}[v1];[1:a:0]volume=-6dB,{stereo}[a1x0];anullsrc=r=48000:cl=stereo,atrim=duration=2.000000[a1x1];" +
            "[v0][a0x0][a0x1][v1][a1x0][a1x1]concat=n=2:v=1:a=2[v][a0][a1]\" " +
            "-map \"[v]\" -map \"[a0]\" -map \"[a1]\" -c:v libx264 -preset medium -crf 18 -c:a aac -b:a 192k -map_chapters 2 " +
            "-map_metadata 0 -movflags +faststart -f mp4 \"{out}/demo-cut.mp4\" -y",
            Args(plan, 0));
    }

    [Fact]
    public void A_re_encoded_join_keeps_the_gpu_encoder()
    {
        var plan = PlanOf(Mixed(Other), Settings(CutMode.Reencode) with { GpuEncoder = GpuEncoder.Nvenc }, Other);
        Assert.Contains("-c:v h264_nvenc -preset p5 -rc vbr -cq 18 -b:v 0 -pix_fmt nv12 -c:a aac", Args(plan, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_unmuted_tracks_silence_a_track_muted_in_one_video()
    {
        // Track 2 is muted in video 1 but not in video 2: it is kept, silent while video 1 plays.
        var settings = Settings(CutMode.Reencode) with
        {
            KeepAllTracks = false,
            SourceAudio = new Dictionary<int, SourceAudioSettings>
            {
                [1] = new([1], new Dictionary<int, double>()),
                [2] = new([1, 2], new Dictionary<int, double>()),
            },
        };

        var plan = PlanOf(Mixed(Part2), settings);

        Assert.Equal([0, 1], plan.Layout!.Lanes.Select(l => l.Position));
        Assert.Contains("[0:a:1]volume=0,aresample", Args(plan, 0), StringComparison.Ordinal);
        Assert.Contains("[1:a:1]aresample", Args(plan, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void Separate_re_encoded_files_keep_each_videos_own_picture()
    {
        var plan = PlanOf(Mixed(Other), Settings(CutMode.Reencode, merge: false), Other);

        Assert.All(plan.Steps, s => Assert.Equal(ExportStepKind.Encode, s.Kind));
        Assert.DoesNotContain("scale", Args(plan, 1), StringComparison.Ordinal);
        Assert.Contains("-i \"{in}/other.mp4\" -t 2.000000 -map 0:0 -map 0:1 -c:v libx264", Args(plan, 1), StringComparison.Ordinal);
    }
}
