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

    /// <summary>The video whose tracks the lanes' headers show: the one under the playhead.</summary>
    private int _laneSourceId;

    /// <summary>What the player was last told for each lane, kept for lanes the video under the playhead does not have.</summary>
    private (bool[] Enabled, double[] Gains)? _appliedTracks;

    /// <summary>
    /// One lane per audio track number (A1, A2, …), as many as the video with the most tracks has. The timeline draws each
    /// video's track N on lane N at that video's own volume; the headers (mute, volume) are those of the video under the
    /// playhead, so each video keeps its own mix. A lane the video there does not have is greyed out.
    /// </summary>
    private void CreateAudioLanes(Project project)
    {
        _volumeTimer?.Stop();
        foreach (var old in AudioLanes)
            old.PropertyChanged -= OnAudioLaneChanged;
        AudioLanes.Clear();
        var source = project.SourceAt(Time) ?? project.Source;
        int sourceId = source?.Id ?? SourceMedia.FirstId;
        _laneSourceId = sourceId;
        var tracks = source?.AudioTracks ?? [];
        int count = project.Sources.IsEmpty ? 0 : project.Sources.Max(s => s.AudioTracks.Length);
        string? video = project.Sources.Count > 1 ? source?.FileName : null;
        for (int i = 0; i < count; i++)
        {
            string key = "A" + (i + 1).ToString(CultureInfo.InvariantCulture);
            if (i >= tracks.Length)
            {
                AudioLanes.Add(new AudioLaneViewModel(i, -1, key, key) { SourceId = sourceId, VideoName = video, HasTrack = false });
                continue;
            }
            var mix = project.MixOf(sourceId, tracks[i].Index);
            var lane = new AudioLaneViewModel(i, tracks[i].Index, key, tracks[i].Label)
            {
                SourceId = sourceId,
                VideoName = video,
                IsMuted = mix.IsMuted,
                GainDb = mix.GainDb,
                EvenOutCommand = EvenOutVolumesCommand,
            };
            lane.PropertyChanged += OnAudioLaneChanged;
            AudioLanes.Add(lane);
        }
        EvenOutVolumesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The playhead moved into another video: the lanes' headers and the player's mix become that video's.</summary>
    private void FollowLaneVideo()
    {
        var project = Session.Project;
        if (project.Sources.Count < 2 || project.SourceAt(Time) is not { } source || source.Id == _laneSourceId)
            return;
        CreateAudioLanes(project);
        ApplyAudioTracks(onlyIfChanged: true);
        OnPropertyChanged(nameof(VideoAspect));
        OnPropertyChanged(nameof(FrameRate));
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
            foreach (var lane in AudioLanes.Where(l => l.HasTrack))
            {
                var mix = project.MixOf(lane.SourceId, lane.Index);
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
    /// <param name="onlyIfChanged">Sends nothing when the player already plays this mix (the playhead crossed a join).</param>
    private void ApplyAudioTracks(bool onlyIfChanged = false)
    {
        _volumeTimer?.Stop();
        if (!HasPlayback)
            return;
        // A lane the video under the playhead does not have plays nothing there; it keeps what it was set to, so the
        // player's mix (and mpv's filter graph, which a change rebuilds with a gap in the sound) stays as it is.
        int n = AudioLanes.Count;
        var previous = _appliedTracks is { } p && p.Enabled.Length == n ? p : (Enumerable.Repeat(true, n).ToArray(), new double[n]);
        bool[] enabled = [.. AudioLanes.Select((l, i) => l.HasTrack ? !l.IsMuted : previous.Item1[i])];
        double[] gains = [.. AudioLanes.Select((l, i) => l.HasTrack ? l.GainDb : previous.Item2[i])];
        if (onlyIfChanged && previous.Item1.SequenceEqual(enabled) && previous.Item2.SequenceEqual(gains) && _appliedTracks is not null)
            return;
        _appliedTracks = (enabled, gains);
        _player!.SetAudioTracks(enabled, gains);
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

    private bool CanEvenOutVolumes() => AudioLanes.Count(l => l.HasTrack && !l.IsMuted) >= 2;

    /// <summary>
    /// Sets the volumes of the unmuted lanes so they sound about equally loud, from the audio's waveform
    /// (<see cref="AudioLevels.EvenOut"/>), which is read first if no chip has read it. Muted lanes keep their volume.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEvenOutVolumes))]
    private async Task EvenOutVolumesAsync()
    {
        // The lanes are the video's under the playhead, and so is the audio measured.
        if ((PreviewOf(_laneSourceId) ?? Media) is not { } media)
            return;
        var editing = Media;
        // Not read yet (no chip asked for it) or still being read.
        if (!media.WaveformRequested || !media.SilencesComplete)
        {
            ShowMessage("Reading the audio to measure how loud each track is…");
            await media.ReadWaveformAsync(CancellationToken.None).ConfigureAwait(true);
            // Another file opened meanwhile (its preview's read ends when it closes): nothing to even out here.
            if (!ReferenceEquals(Media, editing))
                return;
        }
        var lanes = AudioLanes.Where(l => l.HasTrack && !l.IsMuted).ToList();
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
