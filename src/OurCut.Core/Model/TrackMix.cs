namespace OurCut.Core.Model;

/// <summary>
/// How one audio track is mixed: its volume and whether it is muted. Tracks without an entry in
/// <see cref="Project.AudioMix"/> play at 0 dB, unmuted.
/// </summary>
/// <param name="Index">Stream index in the container, as <see cref="AudioTrack.Index"/>.</param>
/// <param name="GainDb">Volume change in dB, <see cref="MinGainDb"/> (silent) to <see cref="MaxGainDb"/>.</param>
/// <param name="IsMuted">Left out of the preview, and of the export when it keeps only unmuted tracks.</param>
public sealed record TrackMix(int Index, double GainDb = 0, bool IsMuted = false)
{
    /// <summary>The bottom of the range, which means silence (−∞ dB).</summary>
    public const double MinGainDb = -40;

    public const double MaxGainDb = 12;

    public bool IsDefault => GainDb == 0 && !IsMuted;

    /// <summary>The volume turned all the way down: the track is silent.</summary>
    public bool IsSilent => GainDb <= MinGainDb;

    /// <summary>Clamps a gain to the range and rounds it to 0.1 dB, so saved values stay tidy.</summary>
    public static double ClampGain(double gainDb) =>
        double.IsNaN(gainDb) ? 0 : Math.Round(Math.Clamp(gainDb, MinGainDb, MaxGainDb), 1);

    /// <summary>Linear factor of a gain: 1 at 0 dB, 0 at <see cref="MinGainDb"/>.</summary>
    public static double Factor(double gainDb) => gainDb <= MinGainDb ? 0 : Math.Pow(10, gainDb / 20);
}
