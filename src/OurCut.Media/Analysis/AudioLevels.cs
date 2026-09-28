using OurCut.Core.Model;
using OurCut.Media.Previews;

namespace OurCut.Media.Analysis;

/// <summary>How loud an audio stream is, measured from its waveform peaks.</summary>
/// <param name="LevelDb">Typical level in dBFS: the power average of the 10 ms peaks that are not silence.</param>
/// <param name="PeakDb">Loudest peak in dBFS.</param>
public readonly record struct AudioLevel(double LevelDb, double PeakDb);

/// <summary>
/// Rough loudness of the audio streams, and the volumes that bring them to about the same loudness ("Even out").
/// The waveform holds one peak per 10 ms of an 8 kHz mono mix of each stream, so this is an estimate, not a
/// loudness meter: good enough to balance a quiet microphone against loud game sound.
/// </summary>
public static class AudioLevels
{
    /// <summary>Peaks below this count as silence and are left out of the level.</summary>
    public const double SilenceDb = -45;

    /// <summary>Gains are rounded to this step, as the volume slider moves.</summary>
    public const double Step = 0.5;

    /// <summary>The level of one stream; null when it is silent throughout (or the stream does not exist).</summary>
    public static AudioLevel? Measure(WaveformData waveform, int stream)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        if (stream < 0 || stream >= waveform.StreamCount)
            return null;
        double floor = Math.Pow(10, SilenceDb / 20);
        double sum = 0, peak = 0;
        long count = 0;
        for (int b = 0, n = Math.Min(waveform.Filled, waveform.Capacity); b < n; b++)
        {
            double p = waveform[stream, b];
            peak = Math.Max(peak, p);
            if (p < floor)
                continue;
            sum += p * p;
            count++;
        }
        return count == 0 ? null : new AudioLevel(10 * Math.Log10(sum / count), 20 * Math.Log10(peak));
    }

    /// <summary>
    /// Volumes (dB, one per stream) that bring the measured streams to the average of their levels: louder streams
    /// come down, quieter ones go up, but never so far that their loudest peak would pass 0 dBFS. Streams that were
    /// not measured (null) keep 0 dB.
    /// </summary>
    public static IReadOnlyList<double> EvenOut(IReadOnlyList<AudioLevel?> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var measured = levels.OfType<AudioLevel>().ToList();
        if (measured.Count == 0)
            return [.. levels.Select(_ => 0.0)];
        double target = measured.Average(l => l.LevelDb);
        return [.. levels.Select(l => l is { } level ? Gain(level, target) : 0)];
    }

    private static double Gain(AudioLevel level, double target)
    {
        double gain = target - level.LevelDb;
        // Headroom: a boost stops where the loudest peak reaches full scale (a cut is never limited).
        if (gain > 0)
            gain = Math.Min(gain, Math.Max(0, -level.PeakDb));
        gain = Math.Clamp(Math.Round(gain / Step) * Step, TrackMix.MinGainDb + Step, TrackMix.MaxGainDb);
        // Rounding can give −0, which would print as "−0 dB".
        return gain == 0 ? 0 : gain;
    }
}
