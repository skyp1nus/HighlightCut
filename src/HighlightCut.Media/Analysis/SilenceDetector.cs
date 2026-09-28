using HighlightCut.Core.Model;
using HighlightCut.Media.Previews;

namespace HighlightCut.Media.Analysis;

/// <summary>Silent ranges of a file and the level that counted as silent.</summary>
/// <param name="ThresholdDb">Peak level (dBFS) below which audio counted as silent.</param>
/// <param name="NoiseFloorDb">Level of the quietest 5 % of the audio (dBFS): roughly the room noise.</param>
/// <param name="IsComplete">False while the audio is still being decoded; the ranges then cover only the decoded part.</param>
public sealed record SilenceAnalysis(IReadOnlyList<TimeRange> Ranges, double ThresholdDb, double NoiseFloorDb, bool IsComplete)
{
    public static SilenceAnalysis None { get; } = new([], double.NegativeInfinity, double.NegativeInfinity, true);

    public double Total => Ranges.Sum(r => r.End - r.Start);
}

/// <summary>
/// Finds silences in the waveform peaks (<see cref="WaveformData"/>, 10 ms buckets): stretches where every
/// audio track stays below a level, like ffmpeg's <c>silencedetect</c> but without decoding the audio again,
/// so the level and minimum length can be changed instantly. By default the level follows the recording's
/// noise floor, so a noisy microphone still has silences and a quiet studio does not lose its pauses.
/// </summary>
public static class SilenceDetector
{
    /// <summary>Shortest pause that counts as a silence, in seconds.</summary>
    public const double DefaultMinDuration = 1.0;

    /// <summary>Levels are measured from −90 dBFS (digital silence) up.</summary>
    public const double FloorDb = -90;

    /// <summary>
    /// The automatic level: a margin above the noise floor, kept between −55 dBFS (very clean recordings) and
    /// −35 dBFS (so quiet music or speech is not taken for silence).
    /// </summary>
    public static double AutoThresholdDb(double noiseFloorDb) => Math.Clamp(noiseFloorDb + 12, -55, -35);

    /// <param name="minDuration">Shortest silence, in seconds.</param>
    /// <param name="thresholdDb">Peak level in dBFS; automatic (from the noise floor) if null.</param>
    /// <param name="streams">Audio streams (0-based) that must all be quiet; all if null.</param>
    public static SilenceAnalysis Find(WaveformData wave, double minDuration = DefaultMinDuration, double? thresholdDb = null,
        IReadOnlyList<int>? streams = null)
    {
        ArgumentNullException.ThrowIfNull(wave);
        int[] use = [.. (streams ?? Enumerable.Range(0, wave.StreamCount)).Where(s => s >= 0 && s < wave.StreamCount).Distinct()];
        int filled = wave.Filled;
        if (use.Length == 0 || filled == 0)
            return SilenceAnalysis.None with { IsComplete = wave.IsComplete || use.Length == 0 };

        var levels = new float[filled];
        for (int b = 0; b < filled; b++)
        {
            float level = 0;
            foreach (int s in use)
                level = Math.Max(level, wave[s, b]);
            levels[b] = level;
        }

        double floor = NoiseFloorDb(levels);
        double threshold = thresholdDb ?? AutoThresholdDb(floor);
        float limit = (float)Math.Pow(10, threshold / 20);
        int minBuckets = Math.Max(1, (int)Math.Round(minDuration * WaveformData.BucketsPerSecond));
        const double Bucket = 1.0 / WaveformData.BucketsPerSecond;

        var ranges = new List<TimeRange>();
        int start = -1;
        for (int b = 0; b <= filled; b++)
        {
            bool silent = b < filled && levels[b] < limit;
            if (silent && start < 0)
            {
                start = b;
            }
            else if (!silent && start >= 0)
            {
                // A silence running into the part still being decoded may go on: only report it when complete.
                if (b - start >= minBuckets && (b < filled || wave.IsComplete))
                    ranges.Add(new TimeRange(start * Bucket, b * Bucket));
                start = -1;
            }
        }
        return new SilenceAnalysis(ranges, Math.Round(threshold, 1), Math.Round(floor, 1), wave.IsComplete);
    }

    /// <summary>The level (dBFS) the quietest 5 % of buckets stay under, from a 1 dB histogram.</summary>
    private static double NoiseFloorDb(float[] levels)
    {
        var histogram = new int[(int)-FloorDb + 1];
        foreach (float level in levels)
            histogram[Bin(level)]++;
        int target = Math.Max(1, (int)Math.Ceiling(levels.Length * 0.05)), seen = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            seen += histogram[i];
            if (seen >= target)
                return FloorDb + i;
        }
        return 0;
    }

    private static int Bin(float level)
    {
        if (level <= 0)
            return 0;
        double db = 20 * Math.Log10(level);
        return (int)Math.Clamp(Math.Ceiling(db - FloorDb), 0, -FloorDb);
    }
}
