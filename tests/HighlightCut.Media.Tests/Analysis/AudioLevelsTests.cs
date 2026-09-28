using HighlightCut.Media.Analysis;
using HighlightCut.Media.Previews;

namespace HighlightCut.Media.Tests.Analysis;

public class AudioLevelsTests
{
    /// <summary>Two streams of 10 s: one peaking at <paramref name="firstDb"/>, one at <paramref name="secondDb"/>, silent half the time.</summary>
    private static WaveformData Wave(double firstDb, double? secondDb)
    {
        var wave = new WaveformData(2, 10);
        for (int b = 0; b < 1000; b++)
        {
            bool sound = b % 2 == 0;
            wave.Set(0, b, sound ? (float)Math.Pow(10, firstDb / 20) : 0);
            wave.Set(1, b, sound && secondDb is { } db ? (float)Math.Pow(10, db / 20) : 0);
        }
        wave.Publish(1000);
        wave.IsComplete = true;
        return wave;
    }

    [Fact]
    public void The_level_leaves_out_silence()
    {
        var level = AudioLevels.Measure(Wave(-6, -30), 0)!.Value;
        Assert.Equal(-6, level.LevelDb, 2);
        Assert.Equal(-6, level.PeakDb, 2);
        Assert.Equal(-30, AudioLevels.Measure(Wave(-6, -30), 1)!.Value.LevelDb, 2);
    }

    [Fact]
    public void A_silent_or_missing_stream_has_no_level()
    {
        Assert.Null(AudioLevels.Measure(Wave(-6, null), 1));
        Assert.Null(AudioLevels.Measure(Wave(-6, null), 2));
    }

    [Fact]
    public void Evening_out_meets_in_the_middle()
    {
        // Game at −10 dB and a microphone at −30 dB: the game comes down 10 dB, the microphone up 10 dB.
        var gains = AudioLevels.EvenOut([new AudioLevel(-10, -2), new AudioLevel(-30, -20)]);
        Assert.Equal([-10.0, 10.0], gains);
    }

    [Fact]
    public void A_boost_stops_at_full_scale()
    {
        // The quiet track's peaks already reach −4 dBFS: it may only come up 4 dB.
        var gains = AudioLevels.EvenOut([new AudioLevel(-10, -1), new AudioLevel(-30, -4)]);
        Assert.Equal([-10.0, 4.0], gains);
    }

    [Fact]
    public void Gains_are_rounded_to_half_a_decibel_and_unmeasured_tracks_stay_at_0_dB()
    {
        var gains = AudioLevels.EvenOut([new AudioLevel(-12.3, -1), null, new AudioLevel(-20, -9)]);
        Assert.Equal([-4.0, 0.0, 4.0], gains);
        Assert.Equal([0.0, 0.0], AudioLevels.EvenOut([null, null]));
    }
}
