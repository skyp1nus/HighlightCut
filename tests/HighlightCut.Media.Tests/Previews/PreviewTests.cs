using HighlightCut.Media.Previews;
using HighlightCut.Media.Probing;
using HighlightCut.Media.Tests.Export;

namespace HighlightCut.Media.Tests.Previews;

public class WaveformDataTests
{
    [Fact]
    public void Has_one_bucket_per_10_ms_for_each_stream()
    {
        var data = new WaveformData(2, 1.234);
        Assert.Equal(2, data.StreamCount);
        Assert.Equal(125, data.Capacity);
        Assert.Equal(0, data.Filled);
        Assert.False(data.IsComplete);
    }

    [Fact]
    public void Peak_is_the_loudest_filled_bucket_on_a_db_scale()
    {
        var data = new WaveformData(1, 1);
        data.Set(0, 10, 0.25f);
        data.Set(0, 20, 1f);
        data.Set(0, 50, 1f);
        data.Publish(30);

        Assert.Equal(1, data.Peak(0, 0.0, 0.3), 9);
        Assert.Equal(WaveformData.ToDisplay(0.25), data.Peak(0, 0.05, 0.15), 9);
        // Bucket 50 is not published yet.
        Assert.Equal(0, data.Peak(0, 0.4, 0.6));
        Assert.Equal(0, data.Peak(1, 0, 1));
    }

    [Fact]
    public void Stretches_decoded_side_by_side_show_as_they_fill()
    {
        var data = new WaveformData(1, 1);
        data.Set(0, 10, 0.5f);
        data.Set(0, 60, 1f);
        data.Set(0, 80, 1f);
        data.Publish([(0, 20), (50, 20)]);

        Assert.Equal(WaveformData.ToDisplay(0.5), data.Peak(0, 0, 0.3), 9);
        Assert.Equal(1, data.Peak(0, 0.5, 0.7), 9);
        // Bucket 80 is past the second stretch's filled part.
        Assert.Equal(0, data.Peak(0, 0.75, 1));
        Assert.Equal(20, data.Filled);
        Assert.Equal(40, data.Decoded);

        data.Publish([(0, 50), (50, 31)]);
        Assert.Equal(81, data.Filled);
        Assert.Equal(1, data.Peak(0, 0.75, 1), 9);
    }

    [Theory]
    [InlineData(10, 8, 1)]
    [InlineData(59, 8, 1)]
    [InlineData(180, 4, 4)]
    [InlineData(180, 16, 6)]
    [InlineData(3600, 16, 8)]
    [InlineData(3600, 1, 1)]
    public void Long_files_are_decoded_in_stretches_of_at_least_30_s(double duration, int cores, int stretches) =>
        Assert.Equal(stretches, WaveformExtractor.StretchesFor(duration, cores));

    [Fact]
    public void Peak_of_a_range_shorter_than_a_bucket_still_reads_one_bucket()
    {
        var data = new WaveformData(1, 1);
        data.Set(0, 10, 0.5f);
        data.Publish(100);
        Assert.Equal(WaveformData.ToDisplay(0.5), data.Peak(0, 0.101, 0.102), 9);
    }

    [Fact]
    public void Publish_never_goes_past_capacity()
    {
        var data = new WaveformData(1, 0.05);
        data.Set(0, 1000, 1f);
        data.Publish(1000);
        Assert.Equal(data.Capacity, data.Filled);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(0.5, 0.874571)]
    [InlineData(0.00398, 0)]
    [InlineData(2, 1)]
    public void Display_scale_maps_minus_48_db_to_zero(double linear, double expected) =>
        Assert.Equal(expected, WaveformData.ToDisplay(linear), 5);

    [Fact]
    public void Extractor_arguments_merge_every_audio_stream()
    {
        var args = WaveformExtractor.Arguments(ExportSample.Info);
        Assert.Equal(
            ["-v", "error", "-i", ExportSample.SourcePath, "-vn", "-sn", "-dn", "-filter_complex",
             "[0:1]aresample=8000,aformat=sample_fmts=s16:channel_layouts=mono[m0];" +
             "[0:2]aresample=8000,aformat=sample_fmts=s16:channel_layouts=mono[m1];[m0][m1]amerge=inputs=2[out]",
             "-map", "[out]", "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"],
            args);
    }

    [Fact]
    public void Extractor_arguments_for_one_stream_use_a_simple_filter()
    {
        var info = ExportSample.Info with { Audio = [ExportSample.Info.Audio[1]] };
        var args = WaveformExtractor.Arguments(info);
        Assert.Equal(["-map", "0:2", "-af", "aresample=8000,aformat=sample_fmts=s16:channel_layouts=mono"], args.Skip(7).Take(4));
    }

    [Fact]
    public void A_stretch_seeks_to_its_start_and_stops_after_its_length()
    {
        var info = ExportSample.Info with { Audio = [ExportSample.Info.Audio[1]] };
        Assert.Equal(["-v", "error", "-ss", "25.01", "-i", ExportSample.SourcePath, "-vn", "-sn", "-dn", "-t", "25.01", "-map", "0:2"],
            WaveformExtractor.Arguments(info, 25.01, 25.01).Take(13));
        Assert.Equal(["-v", "error", "-ss", "50.02", "-i", ExportSample.SourcePath, "-vn", "-sn", "-dn", "-map", "0:2"],
            WaveformExtractor.Arguments(info, 50.02, null).Take(11));
    }
}

public class ThumbnailExtractorTests
{
    private static VideoStreamInfo Video(int w, int h, int rotation = 0) =>
        new(0, "h264", w, h, 30, "30", false, "yuv420p", rotation);

    [Theory]
    [InlineData(3840, 2160, 0, 160)]
    [InlineData(1440, 1080, 0, 120)]
    [InlineData(1920, 1080, 90, 50)]
    [InlineData(0, 0, 0, 160)]
    public void Size_keeps_the_display_aspect_with_an_even_width(int w, int h, int rotation, int expectedWidth)
    {
        var (width, height) = ThumbnailExtractor.SizeFor(Video(w, h, rotation), 90);
        Assert.Equal(expectedWidth, width);
        Assert.Equal(90, height);
        Assert.Equal(0, width % 2);
    }

    [Theory]
    [InlineData(10, 2)]
    [InlineData(600, 2)]
    [InlineData(3000, 10)]
    public void Interval_limits_the_number_of_thumbnails(double duration, double expected) =>
        Assert.Equal(expected, ThumbnailExtractor.IntervalFor(duration), 9);

    [Fact]
    public void Arguments_read_and_decode_only_keyframes_on_the_gpu_when_there_is_one()
    {
        var args = ThumbnailExtractor.Arguments(ExportSample.Info, 160, 90, 2.5);
        Assert.Equal(
            ["-v", "error", "-hwaccel", "auto", "-discard", "nokey", "-skip_frame", "nokey", "-i", ExportSample.SourcePath, "-map", "0:0", "-an", "-sn", "-dn",
             "-vf", "fps=1/2.5:round=near,scale=160:90:flags=bilinear,format=bgra", "-f", "rawvideo", "pipe:1"],
            args);
    }
}

public class WaveformCopyTests
{
    [Fact]
    public void CopyFrom_takes_over_filled_peaks()
    {
        var source = new WaveformData(1, 1);
        source.Set(0, 5, 0.5f);
        source.Publish(50);
        source.IsComplete = true;

        var target = new WaveformData(1, 1);
        target.CopyFrom(source);

        Assert.Equal(50, target.Filled);
        Assert.True(target.IsComplete);
        Assert.Equal(0.5f, target[0, 5]);
    }
}
