using HighlightCut.Media.Tests.Export;
using HighlightCut.Media.Probing;
using HighlightCut.Media.Tools;

namespace HighlightCut.Media.Tests.Probing;

public class MediaProbeTests
{
    private const string Mp4 = """
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "mjpeg", "width": 600, "height": 600,
              "avg_frame_rate": "0/0", "r_frame_rate": "90000/1", "disposition": { "attached_pic": 1 } },
            { "index": 1, "codec_type": "video", "codec_name": "h264", "width": 3840, "height": 2160, "pix_fmt": "yuv420p",
              "avg_frame_rate": "30000/1001", "r_frame_rate": "30000/1001", "has_b_frames": 2, "duration": "872.472800",
              "disposition": { "attached_pic": 0 } },
            { "index": 2, "codec_type": "audio", "codec_name": "aac", "sample_rate": "48000", "channels": 1,
              "tags": { "title": "Mic", "language": "eng" } },
            { "index": 3, "codec_type": "audio", "codec_name": "aac", "sample_rate": "48000", "channels": 2,
              "tags": { "language": "ukr" } },
            { "index": 4, "codec_type": "audio", "codec_name": "aac", "sample_rate": "48000", "channels": 2,
              "tags": { "handler_name": "SoundHandler" } },
            { "index": 5, "codec_type": "data", "codec_name": "bin_data" }
          ],
          "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "872.480000", "start_time": "0.021333",
                      "bit_rate": "44000000", "size": "4800000000" }
        }
        """;

    [Fact]
    public void Parses_duration_start_time_and_the_real_video_stream()
    {
        var info = MediaProbe.Parse(Mp4, "/v/keynote.mp4");
        Assert.Equal(872.48, info.Duration);
        Assert.Equal(0.021333, info.StartTime);
        Assert.Equal(ContainerFamily.Mov, info.Family);
        var v = Assert.IsType<VideoStreamInfo>(info.Video);
        Assert.Equal(1, v.Index);
        Assert.Equal("h264", v.Codec);
        Assert.Equal(30000.0 / 1001, v.FrameRate, 9);
        Assert.Equal("29.97", v.FrameRateText);
        Assert.True(v.HasBFrames);
        Assert.Equal("keynote.mp4 · 4K · 29.97 fps", info.Summary);
    }

    [Fact]
    public void Audio_labels_come_from_title_then_language_then_position()
    {
        var info = MediaProbe.Parse(Mp4, "/v/keynote.mp4");
        Assert.Equal(["Mic", "UKR", "Audio 3"], info.Audio.Select(a => a.Label));
        Assert.Equal([0, 1, 2], info.Audio.Select(a => a.Position));
        var source = info.ToSourceMedia();
        Assert.Equal(["Mic", "UKR", "Audio 3"], source.AudioTracks.Select(t => t.Label));
        Assert.Equal(872.48, source.Duration);
    }

    [Theory]
    [InlineData("\"handler_name\": \"Commentary\"", "Commentary")]
    [InlineData("\"HANDLER_NAME\": \"Game audio\", \"language\": \"eng\"", "Game audio")]
    [InlineData("\"handler_name\": \"Core Media Audio\", \"language\": \"eng\"", "ENG")]
    [InlineData("\"title\": \"Mic\", \"handler_name\": \"Commentary\"", "Mic")]
    public void Mp4_track_names_are_read_from_the_handler_name(string tags, string expected)
    {
        string json = $$"""
            { "streams": [ { "index": 0, "codec_type": "audio", "codec_name": "aac", "tags": { {{tags}} } } ],
              "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "5.0" } }
            """;
        Assert.Equal(expected, MediaProbe.Parse(json, "a.mp4").Audio[0].Label);
    }

    [Fact]
    public void Rotated_video_reports_its_display_size()
    {
        const string json = """
            { "streams": [ { "index": 0, "codec_type": "video", "codec_name": "hevc", "width": 1920, "height": 1080,
                             "avg_frame_rate": "30/1", "side_data_list": [ { "side_data_type": "Display Matrix", "rotation": -90 } ] } ],
              "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "5.0" } }
            """;
        var v = MediaProbe.Parse(json, "/v/phone.mov").Video!;
        Assert.Equal(-90, v.Rotation);
        Assert.Equal((1080, 1920), v.DisplaySize);
    }

    [Theory]
    [InlineData("matroska,webm", ContainerFamily.Matroska)]
    [InlineData("mpegts", ContainerFamily.MpegTs)]
    [InlineData("avi", ContainerFamily.Other)]
    public void Container_family_follows_the_format_name(string format, ContainerFamily expected) =>
        Assert.Equal(expected, MediaInfo.FamilyOf(format));

    [Theory]
    [InlineData("""{ "streams": [], "format": { "format_name": "mp4", "duration": "0" } }""")]
    [InlineData("""{ "streams": [ { "index": 0, "codec_type": "data" } ], "format": { "format_name": "mp4", "duration": "3" } }""")]
    [InlineData("""{ "streams": [] }""")]
    public void Files_without_media_are_rejected(string json) =>
        Assert.Throws<MediaToolException>(() => MediaProbe.Parse(json, "x"));

    [Theory]
    [InlineData("30000/1001", 29.97002997)]
    [InlineData("25/1", 25)]
    [InlineData("0/0", 0)]
    [InlineData("24", 24)]
    [InlineData(null, 0)]
    public void Rational_parses_ffprobe_fractions(string? text, double expected) =>
        Assert.Equal(expected, MediaProbe.Rational(text), 6);
}

public class KeyframeScannerTests
{
    [Theory]
    [InlineData("0.000000,-0.066733,K__", 0.0, true)]
    [InlineData("1.001000,0.967633,___", 1.001, false)]
    [InlineData("N/A,2.002000,K_", 2.002, true)]
    [InlineData("3.003000,2.969633,K__,", 3.003, true)]
    public void ParsePacket_reads_time_and_key_flag(string line, double time, bool key)
    {
        var p = KeyframeScanner.ParsePacket(line);
        Assert.NotNull(p);
        Assert.Equal(time, p!.Value.Time, 6);
        Assert.Equal(key, p.Value.IsKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("N/A,N/A,K_")]
    [InlineData("1.0,1.0,KD")]
    public void ParsePacket_skips_unusable_lines(string line) =>
        Assert.Null(KeyframeScanner.ParsePacket(line));

    [Theory]
    [InlineData("0,          0,          0,      512,   122720, 0x6a12cf23", 0, 0, true)]
    [InlineData("0,        512,       1536,      512,    91293, 0x0600b054, F=0x0", 512, 1536, false)]
    [InlineData("0,      14336,      15360,      512,     4134, 0x16f20fa3, S=1,        8, 0x05c80bc1", 14336, 15360, true)]
    [InlineData("0,      -1024,          0,      512,     4048, 0x5006662b, F=0x3", -1024, 0, true)]
    [InlineData("0, -9223372036854775808,       2048,      512,       10, 0x00000001", 2048, 2048, true)]
    [InlineData("0,       4096, -9223372036854775808,      512,       10, 0x00000001", 4096, 4096, true)]
    public void ParseFrameLine_reads_times_and_key_flag(string line, long dts, long pts, bool key) =>
        Assert.Equal((dts, pts, key), KeyframeScanner.ParseFrameLine(line));

    [Theory]
    [InlineData("#tb 0: 1/15360")]
    [InlineData("")]
    [InlineData("0, 1, 2, 3")]
    [InlineData("0,        512,        512,      512,       10, 0x00000001, F=0x5")]
    [InlineData("0, -9223372036854775808, -9223372036854775808,      512,       10, 0x00000001")]
    public void ParseFrameLine_skips_headers_and_unusable_lines(string line) =>
        Assert.Null(KeyframeScanner.ParseFrameLine(line));

    [Fact]
    public void TimeBase_is_read_from_the_header_of_the_first_stream()
    {
        Assert.Equal((1001L, 30000L), KeyframeScanner.TimeBase("#tb 0: 1001/30000"));
        Assert.Null(KeyframeScanner.TimeBase("#tb 1: 1/48000"));
        Assert.Null(KeyframeScanner.TimeBase("#codec_id 0: h264"));
        Assert.Null(KeyframeScanner.TimeBase("#tb 0: 0/0"));
    }

    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", false, true)]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", true, false)]
    [InlineData("matroska,webm", false, false)]
    [InlineData("mpegts", false, false)]
    public void Only_an_mp4_without_B_frames_skips_to_the_keyframes(string format, bool bFrames, bool skips)
    {
        var info = ExportSample.Info with { FormatName = format, Video = ExportSample.Info.Video! with { HasBFrames = bFrames } };
        Assert.Equal(skips, KeyframeScanner.CanSkipToKeyframes(info));
    }
}
