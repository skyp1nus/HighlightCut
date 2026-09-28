using OurCut.Media.Export;
using static OurCut.Media.Tests.Export.ExportSample;

namespace OurCut.Media.Tests.Export;

/// <summary>Track volumes in the export: only the tracks with a gain are re-encoded; everything else is left as the mode says.</summary>
public class ExportVolumeTests
{
    private static string Args(ExportPlan plan, int step) => FfmpegCommands.ForStep(plan, plan.Steps[step]).Arguments;

    /// <summary>The Music track (stream 2, the second audio stream) 6 dB down.</summary>
    private static ExportSettings MusicDown(CutMode mode = CutMode.Lossless, bool merge = true) =>
        Settings(mode, merge) with { AudioGainsDb = new Dictionary<int, double> { [2] = -6 } };

    [Fact]
    public void A_lossless_merge_copies_the_pieces_and_changes_the_volume_once_when_joining()
    {
        var plan = Plan(MusicDown());
        Assert.DoesNotContain("volume", Args(plan, 0), StringComparison.Ordinal);
        Assert.DoesNotContain("volume", Args(plan, 1), StringComparison.Ordinal);
        Assert.Contains("-map 0 -c copy -map_metadata 0 -filter:a:1 volume=-6dB -c:a:1 aac -b:a:1 192k -map_chapters 1",
            Args(plan, 2), StringComparison.Ordinal);
    }

    [Fact]
    public void A_lossless_clip_re_encodes_only_the_track_with_a_gain()
    {
        string args = Args(Plan(MusicDown(merge: false)), 0);
        Assert.Contains("-map 0:0 -map 0:1 -map 0:2 -map 0:3 -c copy -filter:a:1 volume=-6dB -c:a:1 aac -b:a:1 192k -avoid_negative_ts",
            args, StringComparison.Ordinal);
    }

    [Fact]
    public void A_re_encoded_clip_filters_the_track_and_encodes_it_when_audio_is_copied()
    {
        string copied = Args(Plan(MusicDown(CutMode.Reencode, merge: false)), 0);
        Assert.Contains("-c:a copy -c:s copy -filter:a:1 volume=-6dB -c:a:1 aac -b:a:1 192k -copypriorss 0", copied, StringComparison.Ordinal);

        string aac = Args(Plan(MusicDown(CutMode.Reencode, merge: false) with { Audio = AudioEncoding.Aac192 }), 0);
        Assert.Contains("-c:a aac -b:a 192k -c:s copy -filter:a:1 volume=-6dB -copypriorss 0", aac, StringComparison.Ordinal);
    }

    [Fact]
    public void A_re_encoded_merge_adds_a_volume_filter_after_the_concat()
    {
        string args = Args(Plan(MusicDown(CutMode.Reencode)), 0);
        Assert.Contains(
            "\"[0:v:0][0:a:0][0:a:1][1:v:0][1:a:0][1:a:1]concat=n=2:v=1:a=2[v][a0][c1];[c1]volume=-6dB[a1]\" -map \"[v]\" -map \"[a0]\" -map \"[a1]\"",
            args, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gain_follows_the_track_to_its_output_position()
    {
        // Only Music is kept, so it is the output's first audio stream.
        var settings = MusicDown(merge: false) with { KeepAllTracks = false, AudioStreamIndexes = [2] };
        Assert.Contains("-map 0:0 -map 0:2 -c copy -filter:a:0 volume=-6dB -c:a:0 aac", Args(Plan(settings), 0), StringComparison.Ordinal);
        Assert.Equal([(0, -6.0)], FfmpegCommands.AudioGains(Info, settings));
    }

    [Fact]
    public void Tracks_at_0_dB_and_dropped_tracks_are_left_alone()
    {
        var settings = Settings(merge: false) with
        {
            AudioGainsDb = new Dictionary<int, double> { [1] = 0, [2] = 3 },
            KeepAllTracks = false,
            AudioStreamIndexes = [1],
        };
        Assert.Empty(FfmpegCommands.AudioGains(Info, settings));
        Assert.DoesNotContain("volume", Args(Plan(settings), 0), StringComparison.Ordinal);
    }

    [Fact]
    public void A_silent_track_gets_volume_0() =>
        Assert.Equal("[0:a:0][1:a:0]concat=n=2:v=0:a=1[c0];[c0]volume=0[a0]", FfmpegCommands.ConcatFilter(2, video: false, [0], [-40]));
}
