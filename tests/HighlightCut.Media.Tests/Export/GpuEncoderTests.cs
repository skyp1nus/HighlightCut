using HighlightCut.Media.Export;
using static HighlightCut.Media.Tests.Export.ExportSample;

namespace HighlightCut.Media.Tests.Export;

public class GpuEncoderTests
{
    [Fact]
    public void Each_encoder_gets_constant_quality_near_the_crf()
    {
        Assert.Equal("-c:v h264_nvenc -preset p5 -rc vbr -cq 18 -b:v 0 -pix_fmt nv12", GpuEncoder.Nvenc.Arguments(VideoEncoding.H264Quality));
        Assert.Equal("-c:v h264_nvenc -preset p3 -rc vbr -cq 23 -b:v 0 -pix_fmt nv12", GpuEncoder.Nvenc.Arguments(VideoEncoding.H264Fast));
        Assert.Equal("-c:v hevc_amf -quality quality -rc cqp -qp_i 22 -qp_p 22 -pix_fmt nv12", GpuEncoder.Amf.Arguments(VideoEncoding.H265));
        Assert.Equal("-c:v h264_qsv -preset veryfast -global_quality 23 -pix_fmt nv12", GpuEncoder.QuickSync.Arguments(VideoEncoding.H264Fast));
        Assert.Equal("-c:v h264_videotoolbox -q:v 65 -pix_fmt nv12", GpuEncoder.VideoToolbox.Arguments(VideoEncoding.H264Quality));
    }

    [Fact]
    public void H265_stays_on_the_cpu_where_the_gpu_has_no_hevc_encoder()
    {
        var h264Only = new GpuEncoderSupport(GpuEncoder.QuickSync, Hevc: false);
        Assert.Same(GpuEncoder.QuickSync, h264Only.For(VideoEncoding.H264Quality));
        Assert.Null(h264Only.For(VideoEncoding.H265));
        Assert.Same(GpuEncoder.QuickSync, (h264Only with { Hevc = true }).For(VideoEncoding.H265));
    }

    [Fact]
    public void The_encoder_list_is_read_from_ffmpeg()
    {
        const string list = """
            Encoders:
             V..... = Video
             A..... = Audio
             ------
             V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC (codec h264)
             V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
             A....D aac                  AAC (Advanced Audio Coding)
            """;

        var listed = GpuEncoderProbe.ListedEncoders(list);

        Assert.Equal(["h264_nvenc", "libx264"], listed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_test_encode_uses_the_exports_arguments()
    {
        var args = GpuEncoderProbe.TestArguments(GpuEncoder.Nvenc, VideoEncoding.H265);
        Assert.Equal(["-f", "null", "-"], args.TakeLast(3));
        Assert.Contains("hevc_nvenc", args);
        Assert.Equal("-c:v hevc_nvenc -preset p5 -rc vbr -cq 22 -b:v 0 -pix_fmt nv12",
            string.Join(' ', args.SkipWhile(a => a != "-c:v").SkipLast(3)));
    }

    [Fact]
    public async Task Without_ffmpeg_there_is_no_gpu_encoder() =>
        Assert.Null(await GpuEncoderProbe.DetectAsync(Path.Combine(Path.GetTempPath(), "no-such-dir", "ffmpeg"),
            TestContext.Current.CancellationToken));

    [Fact]
    public void A_gpu_encoder_replaces_x264()
    {
        var plan = Plan(Settings(CutMode.Reencode, merge: false) with { GpuEncoder = GpuEncoder.Nvenc });

        string args = FfmpegCommands.ForStep(plan, plan.Steps[0]).Arguments;

        Assert.Contains("-c:v h264_nvenc -preset p5 -rc vbr -cq 18 -b:v 0 -pix_fmt nv12 -c:a copy", args, StringComparison.Ordinal);
        Assert.DoesNotContain("libx264", args, StringComparison.Ordinal);
    }
}
