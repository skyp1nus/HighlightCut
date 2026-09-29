using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.Core.Model;
using HighlightCut.Media.Analysis;

namespace HighlightCut.App.ViewModels;

/// <summary>
/// The audio lanes' mute and volume. They are stored in the project (<see cref="Project.AudioMix"/>), so they are saved
/// with it, but like mute before them they are mixer settings rather than edits: not in the undo history.
/// </summary>
public sealed partial class EditorViewModel
{
    private DispatcherTimer? _volumeTimer;
    private bool _syncingLanes;

    /// <summary>
    /// How long a volume change waits before the player hears it. mpv rebuilds its audio filters for every change (a
    /// short gap in the sound), so a slider drag is applied a few times a second rather than on every step. Zero in tests.
    /// </summary>
    public TimeSpan VolumeApplyDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>One lane per audio stream of the project's source, with the mix the project has for it.</summary>
    private void CreateAudioLanes(Project project)
    {
        _volumeTimer?.Stop();
        AudioLanes.Clear();
        var tracks = project.Source?.AudioTracks ?? [];
        for (int i = 0; i < tracks.Length; i++)
        {
            var mix = project.MixOf(tracks[i].Index);
            var lane = new AudioLaneViewModel(i, tracks[i].Index, "A" + (i + 1).ToString(CultureInfo.InvariantCulture), tracks[i].Label)
            {
                IsMuted = mix.IsMuted,
                GainDb = mix.GainDb,
                EvenOutCommand = EvenOutVolumesCommand,
            };
            lane.PropertyChanged += OnAudioLaneChanged;
            AudioLanes.Add(lane);
        }
        EvenOutVolumesCommand.NotifyCanExecuteChanged();
    }

    private void OnAudioLaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AudioLaneViewModel lane)
            return;
        if (e.PropertyName is nameof(AudioLaneViewModel.IsMuted) or nameof(AudioLaneViewModel.GainDb))
        {
            if (!_syncingLanes)
                Session.SetTrackMix(lane.Mix);
            if (e.PropertyName == nameof(AudioLaneViewModel.IsMuted))
            {
                ApplyAudioTracks();
                EvenOutVolumesCommand.NotifyCanExecuteChanged();
            }
            else
                ScheduleAudioTracks();
        }
        RaiseTimelineChanged();
    }

    /// <summary>Shows the project's mix on the lanes (when it changed elsewhere, e.g. the project was saved clamped).</summary>
    private void SyncAudioLanes(Project project)
    {
        _syncingLanes = true;
        try
        {
            foreach (var lane in AudioLanes)
            {
                var mix = project.MixOf(lane.Index);
                lane.IsMuted = mix.IsMuted;
                lane.GainDb = mix.GainDb;
            }
        }
        finally
        {
            _syncingLanes = false;
        }
    }

    /// <summary>Muted lanes are left out of what the player plays; the others play at their volume.</summary>
    private void ApplyAudioTracks()
    {
        _volumeTimer?.Stop();
        if (HasPlayback)
            _player!.SetAudioTracks([.. AudioLanes.Select(l => !l.IsMuted)], [.. AudioLanes.Select(l => l.GainDb)]);
    }

    /// <summary>Applies the volumes after <see cref="VolumeApplyDelay"/>; changes meanwhile go with it.</summary>
    private void ScheduleAudioTracks()
    {
        if (VolumeApplyDelay <= TimeSpan.Zero)
        {
            ApplyAudioTracks();
            return;
        }
        if (_volumeTimer?.IsEnabled == true)
            return;
        _volumeTimer ??= new DispatcherTimer(VolumeApplyDelay, DispatcherPriority.Background, (_, _) => ApplyAudioTracks());
        _volumeTimer.Interval = VolumeApplyDelay;
        _volumeTimer.Start();
    }

    private bool CanEvenOutVolumes() => AudioLanes.Count(l => !l.IsMuted) >= 2;

    /// <summary>
    /// Sets the volumes of the unmuted lanes so they sound about equally loud, from the audio's waveform
    /// (<see cref="AudioLevels.EvenOut"/>), which is read first if no chip has read it. Muted lanes keep their volume.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEvenOutVolumes))]
    private async Task EvenOutVolumesAsync()
    {
        if (Media is not { } media)
            return;
        // Not read yet (no chip asked for it) or still being read.
        if (!media.WaveformRequested || !media.SilencesComplete)
        {
            ShowMessage("Reading the audio to measure how loud each track is…");
            await media.ReadWaveformAsync(CancellationToken.None).ConfigureAwait(true);
            // Another file opened meanwhile (its preview's read ends when it closes): nothing to even out here.
            if (!ReferenceEquals(Media, media))
                return;
        }
        var lanes = AudioLanes.Where(l => !l.IsMuted).ToList();
        var levels = lanes.Select(l => media.MeasureAudio(l.Stream)).ToList();
        if (levels.Count(l => l is not null) < 2)
        {
            ShowMessage(levels.All(l => l is null)
                ? "Could not read the audio, so the volumes stay as they are."
                : "Only one track has sound, so there is nothing to even out.");
            return;
        }
        var gains = AudioLevels.EvenOut(levels);
        for (int i = 0; i < lanes.Count; i++)
            lanes[i].GainDb = gains[i];
        ShowMessage("Evened out: " + string.Join(", ", lanes.Select(l => $"{l.Label} {l.GainText} dB")) + ".");
    }
}
