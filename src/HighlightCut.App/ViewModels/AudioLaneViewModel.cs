using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.Core.Model;

namespace HighlightCut.App.ViewModels;

/// <summary>An audio stream of the source file, shown as a lane under the video, with its mute and volume.</summary>
/// <param name="stream">Position among the file's audio streams (0-based).</param>
/// <param name="index">Stream index in the container (<see cref="AudioTrack.Index"/>), which the project's mix is keyed by.</param>
public sealed partial class AudioLaneViewModel(int stream, int index, string key, string label) : ViewModelBase
{
    public int Stream { get; } = stream;
    public int Index { get; } = index;
    public string Key { get; } = key;
    public string Label { get; } = label;

    /// <summary>The video the stream belongs to (<see cref="SourceMedia.Id"/>), which the project's mix is keyed by too.</summary>
    public int SourceId { get; init; } = SourceMedia.FirstId;

    /// <summary>Muted in the preview. Muted lanes are drawn at 30 % opacity.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteTip))]
    public partial bool IsMuted { get; set; }

    /// <summary>
    /// Volume in dB, <see cref="TrackMix.MinGainDb"/> (silent) to <see cref="TrackMix.MaxGainDb"/>. It applies to the
    /// preview and the export; the waveform is drawn as it will sound.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText), nameof(VolumeTip))]
    public partial double GainDb { get; set; }

    public string MuteTip => IsMuted ? "Unmute" : "Mute";

    /// <summary>The volume next to the slider: "0", "+3", "−6.5", "−∞".</summary>
    public string GainText => FormatGain(GainDb);

    public string VolumeTip =>
        $"{Label} volume: {(GainDb <= TrackMix.MinGainDb ? "silent" : GainText + " dB")}. Scroll to fine-tune, double-click to reset to 0 dB, "
        + "right-click to even out the tracks.";

    /// <summary>The editor's "Even out all tracks", offered in the lane's context menu.</summary>
    public System.Windows.Input.ICommand? EvenOutCommand { get; init; }

    /// <summary>The lane's settings as the project stores them.</summary>
    public TrackMix Mix => new(Index, GainDb, IsMuted, SourceId);

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    private void ResetVolume() => GainDb = 0;

    /// <summary>Moves the volume by <paramref name="steps"/> half-decibels (the mouse wheel), within the range.</summary>
    public void Nudge(double steps) => GainDb = Math.Clamp(Math.Round(GainDb * 2 + steps) / 2, TrackMix.MinGainDb, TrackMix.MaxGainDb);

    public static string FormatGain(double gainDb)
    {
        if (gainDb <= TrackMix.MinGainDb)
            return "−∞";
        double rounded = Math.Round(gainDb, 1);
        if (rounded == 0)
            return "0";
        string text = Math.Abs(rounded).ToString("0.#", CultureInfo.InvariantCulture);
        return (rounded > 0 ? "+" : "−") + text;
    }
}
