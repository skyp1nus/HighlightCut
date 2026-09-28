using HighlightCut.Media.Export;
using HighlightCut.Media.Probing;
using static HighlightCut.Media.Tests.Export.ExportSample;

namespace HighlightCut.Media.Tests.Export;

public class FfmpegCommandsTests
{
    private static string Q(string path) => $"\"{path}\"";

    private static string Args(ExportPlan plan, int step) => FfmpegCommands.ForStep(plan, plan.Steps[step]).Arguments;

    [Fact]
    public void Lossless_cut_seeks_before_the_input_and_copies_all_streams()
    {
        var plan = Plan(Settings());
        Assert.Equal(
            $"-ss 1.001000 -i {Q(SourcePath)} -t 2.199000 -map 0:0 -map 0:1 -map 0:2 -map 0:3 -c copy -avoid_negative_ts make_zero " +
            $"-map_metadata 0 -ignore_unknown -f mp4 {Q(Out(".highlightcut-tmp-t1-001.mp4"))} -y",
            Args(plan, 0));
    }

    [Fact]
    public void Concat_joins_the_list_and_adds_chapters()
    {
        var plan = Plan(Settings());
        Assert.Equal(
            $"-f concat -safe 0 -i {Q(Out(".highlightcut-tmp-t1.ffconcat"))} -f ffmetadata -i {Q(Out(".highlightcut-tmp-t1.ffmeta"))} " +
            $"-map 0 -c copy -map_metadata 0 -map_chapters 1 -movflags +faststart -f mp4 {Q(Out("demo-cut.mp4"))} -y",
            Args(plan, 2));
    }

    [Fact]
    public void Concat_without_chapters_has_one_input()
    {
        var plan = Plan(Settings() with { AddChapters = false, Container = OutputContainer.Mkv });
        Assert.Equal(
            $"-f concat -safe 0 -i {Q(Out(".highlightcut-tmp-t1.ffconcat"))} -map 0 -c copy -map_metadata 0 -f matroska {Q(Out("demo-cut.mkv"))} -y",
            Args(plan, 2));
    }

    [Fact]
    public void Separate_lossless_files_get_faststart()
    {
        var plan = Plan(Settings(merge: false));
        Assert.EndsWith($"-movflags +faststart -f mp4 {Q(Out("demo-cut-01.mp4"))} -y", Args(plan, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void Reencoded_merge_uses_one_seeked_input_per_clip_and_the_concat_filter()
    {
        var plan = Plan(Settings(CutMode.Reencode));
        Assert.Equal(
            $"-ss 1.500000 -t 1.700000 -i {Q(SourcePath)} -ss 5.000000 -t 2.000000 -i {Q(SourcePath)} " +
            $"-f ffmetadata -i {Q(Out(".highlightcut-tmp-t1.ffmeta"))} " +
            "-filter_complex \"[0:v:0][0:a:0][0:a:1][1:v:0][1:a:0][1:a:1]concat=n=2:v=1:a=2[v][a0][a1]\" " +
            "-map \"[v]\" -map \"[a0]\" -map \"[a1]\" -c:v libx264 -preset medium -crf 18 -c:a aac -b:a 192k " +
            $"-map_chapters 2 -map_metadata 0 -movflags +faststart -f mp4 {Q(Out("demo-cut.mp4"))} -y",
            Args(plan, 0));
    }

    [Fact]
    public void Reencoded_clip_is_frame_accurate_and_can_copy_audio()
    {
        var plan = Plan(Settings(CutMode.Reencode, merge: false));
        Assert.Equal(
            $"-ss 1.500000 -i {Q(SourcePath)} -t 1.700000 -map 0:0 -map 0:1 -map 0:2 -map 0:3 -c:v libx264 -preset medium -crf 18 " +
            $"-c:a copy -c:s copy -copypriorss 0 -map_metadata 0 -movflags +faststart -f mp4 {Q(Out("demo-cut-01.mp4"))} -y",
            Args(plan, 0));
    }

    [Fact]
    public void Only_selected_audio_is_kept_when_not_keeping_all_tracks()
    {
        var settings = Settings() with { KeepAllTracks = false, AudioStreamIndexes = [2] };
        Assert.Contains("-map 0:0 -map 0:2 -c copy", Args(Plan(settings), 0), StringComparison.Ordinal);

        var merged = Plan(settings with { Mode = CutMode.Reencode });
        string args = Args(merged, 0);
        Assert.Contains("\"[0:v:0][0:a:1][1:v:0][1:a:1]concat=n=2:v=1:a=1[v][a0]\" -map \"[v]\" -map \"[a0]\" -c:v", args,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Muting_every_track_exports_video_only()
    {
        var settings = Settings(CutMode.Reencode, merge: false) with { KeepAllTracks = false, AudioStreamIndexes = [] };
        string args = Args(Plan(settings), 0);
        Assert.Contains("-map 0:0 -c:v", args, StringComparison.Ordinal);
        Assert.DoesNotContain("-c:a", args, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OutputContainer.Mp4, "mov_text", true)]
    [InlineData(OutputContainer.Mov, "mov_text", true)]
    [InlineData(OutputContainer.Mp4, "subrip", false)]
    [InlineData(OutputContainer.Mkv, "subrip", true)]
    [InlineData(OutputContainer.Mkv, "hdmv_pgs_subtitle", true)]
    [InlineData(OutputContainer.Mkv, "mov_text", false)]
    public void Subtitles_are_kept_only_where_the_container_can_hold_them(OutputContainer container, string codec, bool kept)
    {
        var info = Info with { Subtitles = [new SubtitleStreamInfo(3, codec, "eng")] };
        var maps = FfmpegCommands.StreamMaps(info, Settings() with { Container = container });
        Assert.Equal(kept, maps.Contains("-map 0:3"));
        Assert.DoesNotContain("-map 0:3", FfmpegCommands.StreamMaps(info, Settings() with { Container = container, KeepAllTracks = false }));
    }

    [Fact]
    public void Reencoding_copies_kept_subtitles()
    {
        var info = Info with { Subtitles = [new SubtitleStreamInfo(3, "subrip", "eng")] };
        var settings = Settings(CutMode.Reencode, merge: false) with { Container = OutputContainer.Mkv };
        string args = Args(Plan(settings, info: info), 0);
        Assert.Contains("-map 0:3 -c:v libx264", args, StringComparison.Ordinal);
        Assert.Contains("-c:a copy -c:s copy", args, StringComparison.Ordinal);
    }

    [Fact]
    public void Ten_bit_sources_are_converted_for_players()
    {
        var info = Info with { Video = Info.Video! with { PixelFormat = "yuv420p10le" } };
        Assert.Contains("-crf 18 -pix_fmt yuv420p", Args(Plan(Settings(CutMode.Reencode), info: info), 0), StringComparison.Ordinal);
    }

    [Fact]
    public void Hevc_in_mp4_is_tagged_for_apple_players()
    {
        var settings = Settings(CutMode.Reencode) with { Video = VideoEncoding.H265 };
        Assert.Contains("-c:v libx265 -preset medium -crf 22 -tag:v hvc1", Args(Plan(settings), 0), StringComparison.Ordinal);
        Assert.DoesNotContain("hvc1", Args(Plan(settings with { Container = OutputContainer.Mkv }), 0), StringComparison.Ordinal);
    }

    [Fact]
    public void Concat_filter_for_audio_only_sources() =>
        Assert.Equal("[0:a:0][1:a:0]concat=n=2:v=0:a=1[a0]", FfmpegCommands.ConcatFilter(2, video: false, [0]));

    [Fact]
    public void Commands_do_not_depend_on_the_current_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("uk-UA");
            Assert.Contains("-ss 1.001000", Args(Plan(Settings()), 0), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}

public class FfmpegFilesTests
{
    [Fact]
    public void Concat_list_uses_absolute_file_urls_and_escapes_quotes()
    {
        string a = Out("a.mp4"), b = Out("it's.mp4");
        Assert.Equal(
            $"ffconcat version 1.0\nfile 'file:{a}'\nfile 'file:{Out("it'\\''s.mp4")}'\n",
            FfmpegFiles.ConcatList([a, b]));
    }

    [Fact]
    public void Chapters_are_back_to_back_in_milliseconds()
    {
        string text = FfmpegFiles.Chapters([("Intro", 2.2), ("Demo = import; #2", 2.0)]);
        Assert.Equal(
            ";FFMETADATA1\n" +
            "\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=2200\ntitle=Intro\n" +
            "\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=2200\nEND=4200\ntitle=Demo \\= import\\; \\#2\n",
            text);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData(@"a\b", @"a\\b")]
    [InlineData("line\nbreak", "line\\\nbreak")]
    [InlineData("cr\r", "cr ")]
    public void Metadata_values_are_escaped(string value, string expected) =>
        Assert.Equal(expected, FfmpegFiles.EscapeMetadata(value));
}
