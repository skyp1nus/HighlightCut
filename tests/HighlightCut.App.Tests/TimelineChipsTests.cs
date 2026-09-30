using System.Collections.Immutable;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using HighlightCut.App.Demo;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.Transcription;
using HighlightCut.Transcription.Models;

namespace HighlightCut.App.Tests;

/// <summary>
/// The timeline toolbar's chips: kept for every project and every run, and only what they show is worked out (keyframes
/// while Keyframes is on, the audio while Waveform or Silence is, thumbnails while Frames is, scene changes while Scenes
/// is, a transcript while Transcript is). Waveform and Snap start on, the rest off: opening a video reads its audio in the
/// background and nothing else.
/// </summary>
public sealed class TimelineChipsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-chips").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string SettingsFile => Path.Combine(_dir, "settings.json");

    /// <summary>An editor as the app starts one: the settings file read, and changes saved to it.</summary>
    private EditorViewModel Start(IMediaOpener? opener = null)
    {
        var store = new AppSettingsStore(SettingsFile);
        var editor = App.CreateEditor(null, opener);
        editor.Settings.Load(store.Load());
        editor.Settings.Store = store;
        return editor;
    }

    [AvaloniaFact]
    public void The_chips_are_kept_for_the_next_run()
    {
        var first = Start();
        Assert.True(first.SnapToKeyframes && first.ShowWaveform);
        Assert.False(first.ShowKeyframes || first.ShowSilences || first.ShowFrames || first.ShowScenes || first.ShowTranscriptLane);
        Assert.Equal("Show scene changes (finding them reads every frame, so it takes a while)", first.ScenesTip);
        Assert.StartsWith("Show keyframes: the frames a clip can start on without re-encoding.", first.KeyframesTip, StringComparison.Ordinal);
        Assert.Equal("Audio waveform: read in every video you open", first.WaveformTip);
        Assert.Equal("Show pauses of a second or more (finding them reads all the audio)", first.SilenceTip);

        first.ToggleFramesCommand.Execute(null);
        first.ToggleKeyframesCommand.Execute(null);
        first.ToggleWaveformCommand.Execute(null);
        Assert.Equal("Show the audio waveform: where it gets loud or goes quiet (the audio is read while the video plays)", first.WaveformTip);
        first.ToggleScenesCommand.Execute(null);
        first.ToggleSnapCommand.Execute(null);
        first.ToggleTranscriptLaneCommand.Execute(null);
        Assert.Equal("Scene changes: found in every video you open", first.ScenesTip);
        Assert.Equal("Keyframe ticks: found in every video you open; trims snap to them", first.KeyframesTip);

        var saved = new AppSettingsStore(SettingsFile).Load();
        Assert.Equal(new TimelineSettings(Keyframes: true, Silences: false, Scenes: true, Snap: false, Frames: true, Waveform: false),
            saved.Timeline);
        Assert.True(saved.Transcription.TranscribeOnOpen);
        var second = Start();
        Assert.True(second.ShowFrames);
        Assert.True(second.ShowKeyframes);
        Assert.False(second.ShowWaveform);
        Assert.False(second.ShowSilences);
        Assert.True(second.ScenesOn);
        Assert.False(second.SnapToKeyframes);
        Assert.True(second.ShowTranscriptLane);
    }

    [AvaloniaFact]
    public async Task Scene_changes_are_found_only_while_the_Scenes_chip_is_on()
    {
        var opener = new CountingOpener();
        var editor = Start(opener);
        await editor.OpenMediaAsync("/videos/talk.mp4");
        var talk = opener.Last!;
        Assert.Equal(0, talk.SceneSearches);

        editor.ToggleScenesCommand.Execute(null);
        Assert.Equal(1, talk.SceneSearches);
        editor.ToggleScenesCommand.Execute(null);
        Assert.Equal(1, talk.SceneStops);
        await editor.OpenMediaAsync("/videos/other.mp4");
        Assert.Equal(0, opener.Last!.SceneSearches);

        // On, every video opened after is searched too, in this run and the next.
        editor.ToggleScenesCommand.Execute(null);
        await editor.OpenMediaAsync("/videos/third.mp4");
        Assert.Equal(1, opener.Last!.SceneSearches);
        var next = Start(opener);
        await next.OpenMediaAsync("/videos/talk.mp4");
        Assert.Equal(1, opener.Last!.SceneSearches);
    }

    [AvaloniaFact]
    public void A_settings_file_from_before_the_Frames_chip_reads_as_off()
    {
        File.WriteAllText(SettingsFile, """{ "transcription": {}, "timeline": { "keyframes": false, "scenes": true } }""");

        var editor = Start();

        Assert.False(editor.ShowFrames);
        Assert.False(editor.ShowKeyframes);
        Assert.True(editor.ShowScenes);
    }

    [AvaloniaFact]
    public async Task Thumbnails_are_made_only_while_the_Frames_chip_is_on()
    {
        var opener = new CountingOpener();
        var editor = Start(opener);
        await editor.OpenMediaAsync("/videos/talk.mp4");
        var talk = opener.Last!;
        Assert.Equal(0, talk.ThumbnailRuns);
        Assert.False(talk.ThumbnailsRequested);

        editor.ToggleFramesCommand.Execute(null);
        Assert.Equal(1, talk.ThumbnailRuns);
        editor.ToggleFramesCommand.Execute(null);
        Assert.Equal(1, talk.ThumbnailStops);
        Assert.False(talk.ThumbnailsRequested);
        await editor.OpenMediaAsync("/videos/other.mp4");
        Assert.Equal(0, opener.Last!.ThumbnailRuns);

        // On, every video opened after gets them as it opens, in this run and the next.
        editor.ToggleFramesCommand.Execute(null);
        await editor.OpenMediaAsync("/videos/third.mp4");
        Assert.Equal(1, opener.Last!.ThumbnailRuns);
        var next = Start(opener);
        await next.OpenMediaAsync("/videos/talk.mp4");
        Assert.Equal(1, opener.Last!.ThumbnailRuns);
        Assert.Equal(0, opener.Last!.ThumbnailStops);
    }

    [AvaloniaFact]
    public async Task The_Transcript_chip_is_transcribe_when_a_video_is_opened()
    {
        string parakeet = Directory.CreateDirectory(Path.Combine(_dir, "models", ModelCatalog.Parakeet.Id)).FullName;
        foreach (string file in ModelCatalog.Parakeet.Files)
            File.WriteAllText(Path.Combine(parakeet, file), "");
        var opener = new CountingOpener();
        var editor = Start(opener);
        editor.Settings.ModelsFolder = Path.Combine(_dir, "models");
        await editor.OpenMediaAsync("/videos/talk.mp4");
        var talk = opener.Last!;
        Assert.Equal(0, talk.Transcriptions);

        editor.ToggleTranscriptLaneCommand.Execute(null);
        Assert.True(editor.Settings.TranscribeOnOpen);
        Assert.Equal(1, talk.Transcriptions);

        // Off stops the transcription under way, and the next video is left alone.
        editor.ToggleTranscriptLaneCommand.Execute(null);
        Assert.False(editor.Settings.TranscribeOnOpen);
        Assert.Equal(1, talk.TranscriptionStops);
        Assert.False(new AppSettingsStore(SettingsFile).Load().Transcription.TranscribeOnOpen);
        await editor.OpenMediaAsync("/videos/other.mp4");
        Assert.Equal(0, opener.Last!.Transcriptions);

        // The setting in Settings → Transcription lights the chip.
        editor.Settings.TranscribeOnOpen = true;
        Assert.True(editor.ShowTranscriptLane);
        Assert.Equal(1, opener.Last!.Transcriptions);
    }

    [AvaloniaFact]
    public async Task The_design_shows_every_chip_and_keeps_none()
    {
        var editor = App.CreateEditor(DesignScreen.Editing, new CountingOpener());
        editor.Settings.Store = new AppSettingsStore(SettingsFile);
        Assert.True(editor.ShowKeyframes && editor.ShowWaveform && editor.ShowSilences && editor.ShowScenes && editor.ShowFrames
                    && editor.SnapToKeyframes);

        editor.ToggleKeyframesCommand.Execute(null);
        Assert.False(File.Exists(SettingsFile));

        // Opening a file leaves the design: the user's own chips (here the defaults, Waveform on) come back.
        await editor.OpenMediaAsync("/videos/talk.mp4");
        Assert.False(editor.IsDemo);
        Assert.True(editor.ShowWaveform);
        Assert.False(editor.ShowKeyframes || editor.ShowSilences || editor.ShowScenes || editor.ShowFrames);
        var talk = (CountingOpener.Preview)editor.Media!;
        Assert.Equal((0, 1, 0), (talk.KeyframeScans, talk.WaveformReads, talk.ThumbnailRuns));
        Assert.False(File.Exists(SettingsFile));
    }

    [AvaloniaFact]
    public async Task Opening_a_video_with_the_default_chips_reads_only_the_audio()
    {
        var opener = new CountingOpener();
        var editor = Start(opener);
        await editor.OpenMediaAsync("/videos/talk.mp4");
        var talk = opener.Last!;

        Assert.Equal((0, 1, 0, 0, 0), (talk.KeyframeScans, talk.WaveformReads, talk.ThumbnailRuns, talk.SceneSearches, talk.Transcriptions));
        Assert.False(editor.Processing.IsVisible);
        Assert.Contains("Timeline chips keyframes off · waveform on · silence off · scenes off · frames off",
            editor.Settings.DiagnosticsText(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Keyframes_are_scanned_only_while_the_Keyframes_chip_is_on()
    {
        var opener = new CountingOpener();
        var editor = Start(opener);
        await editor.OpenMediaAsync("/videos/talk.mp4");
        var talk = opener.Last!;

        editor.ToggleKeyframesCommand.Execute(null);
        Assert.Equal(1, talk.KeyframeScans);
        Assert.True(editor.KeyframesOn);
        editor.ToggleKeyframesCommand.Execute(null);
        Assert.Equal(1, talk.KeyframeStops);
        await editor.OpenMediaAsync("/videos/other.mp4");
        Assert.Equal(0, opener.Last!.KeyframeScans);

        // On, every video opened after is scanned as it opens, in this run and the next.
        editor.ToggleKeyframesCommand.Execute(null);
        await editor.OpenMediaAsync("/videos/third.mp4");
        Assert.Equal(1, opener.Last!.KeyframeScans);
        var next = Start(opener);
        await next.OpenMediaAsync("/videos/talk.mp4");
        Assert.Equal(1, opener.Last!.KeyframeScans);
    }

    [AvaloniaFact]
    public async Task The_audio_is_read_while_the_Waveform_or_the_Silence_chip_is_on()
    {
        var opener = new CountingOpener();
        var editor = Start(opener);
        await editor.OpenMediaAsync("/videos/talk.mp4");
        var talk = opener.Last!;

        // On by default, the Waveform chip read it as the video opened; off, nothing more is read.
        Assert.Equal(1, talk.WaveformReads);
        editor.ToggleWaveformCommand.Execute(null);
        Assert.Equal(1, talk.WaveformStops);
        await editor.OpenMediaAsync("/videos/quiet.mp4");
        talk = opener.Last!;
        Assert.Equal(0, talk.WaveformReads);

        // Silences are found in the waveform, so either chip reads it; it stops only when both are off.
        editor.ToggleSilencesCommand.Execute(null);
        Assert.Equal(1, talk.WaveformReads);
        editor.ToggleWaveformCommand.Execute(null);
        Assert.Equal(2, talk.WaveformReads);
        editor.ToggleSilencesCommand.Execute(null);
        Assert.Equal(0, talk.WaveformStops);
        Assert.True(talk.WaveformRequested);
        editor.ToggleWaveformCommand.Execute(null);
        Assert.Equal(1, talk.WaveformStops);
        Assert.False(talk.WaveformRequested);

        editor.ToggleSilencesCommand.Execute(null);
        await editor.OpenMediaAsync("/videos/other.mp4");
        Assert.Equal(1, opener.Last!.WaveformReads);
        Assert.Equal(0, opener.Last!.KeyframeScans);
    }

    [AvaloniaFact]
    public async Task Evening_out_the_volumes_reads_the_audio_when_no_chip_has()
    {
        var opener = new CountingOpener(streams: 2);
        var editor = Start(opener);
        editor.ToggleWaveformCommand.Execute(null);
        await editor.OpenMediaAsync("/videos/talk.mp4");
        var talk = opener.Last!;

        editor.EvenOutVolumesCommand.Execute(null);

        Assert.Equal(1, talk.WaveformWaits);
        Assert.Equal("Could not read the audio, so the volumes stay as they are.", editor.StatusMessage);
    }

    [AvaloniaFact]
    public void A_settings_file_from_when_keyframes_and_silences_were_on_by_default_reads_as_off()
    {
        File.WriteAllText(SettingsFile, """{ "transcription": {}, "timeline": { "keyframes": true, "silences": true, "snap": true, "frames": true } }""");

        var editor = Start();

        // Read once with the new defaults; the other chips stay as they were.
        Assert.False(editor.ShowKeyframes || editor.ShowSilences);
        Assert.True(editor.ShowFrames && editor.SnapToKeyframes && editor.ShowWaveform);

        // From then on the user's choice is kept.
        editor.ToggleKeyframesCommand.Execute(null);
        editor.ToggleSilencesCommand.Execute(null);
        Assert.Contains("\"keyframeTicks\": true", File.ReadAllText(SettingsFile), StringComparison.Ordinal);
        var next = Start();
        Assert.True(next.ShowKeyframes && next.ShowSilences && next.ShowWaveform);
    }

    [AvaloniaFact]
    public void A_settings_file_from_when_the_waveform_was_off_by_default_reads_as_on()
    {
        File.WriteAllText(SettingsFile, """{ "transcription": {}, "timeline": { "scenes": true, "snap": false, "waveform": false } }""");

        var editor = Start();

        // Read once with the new default; the other chips stay as they were.
        Assert.True(editor.ShowWaveform);
        Assert.True(editor.ShowScenes);
        Assert.False(editor.SnapToKeyframes);
    }

    [AvaloniaFact]
    public void The_waveform_turned_off_stays_off()
    {
        var first = Start();
        Assert.True(first.ShowWaveform);

        first.ToggleWaveformCommand.Execute(null);
        Assert.Contains("\"waveformBars\": false", File.ReadAllText(SettingsFile), StringComparison.Ordinal);

        var next = Start();
        Assert.False(next.ShowWaveform);
        Assert.False(new AppSettingsStore(SettingsFile).Load().Timeline!.Waveform);
    }

    /// <summary>Opens every path as a small file that counts what is asked of it.</summary>
    private sealed class CountingOpener(int streams = 1) : IMediaOpener
    {
        public Preview? Last { get; private set; }

        public Task<OpenedMedia> OpenAsync(string path, CancellationToken cancellationToken = default)
        {
            Last = new Preview(streams);
            var tracks = Enumerable.Range(1, streams).Select(i => new HighlightCut.Core.Model.AudioTrack(i, "Mic " + i)).ToImmutableArray();
            return Task.FromResult(new OpenedMedia(new HighlightCut.Core.Model.SourceMedia(path, 10, 25, tracks), Last, "talk.mp4"));
        }

        public sealed class Preview(int streams) : IMediaPreview
        {
            public int KeyframeScans { get; private set; }
            public int KeyframeStops { get; private set; }
            public int WaveformReads { get; private set; }
            public int WaveformStops { get; private set; }
            public int WaveformWaits { get; private set; }
            public bool WaveformRequested { get; private set; }
            public int SceneSearches { get; private set; }
            public int SceneStops { get; private set; }
            public int Transcriptions { get; private set; }
            public int TranscriptionStops { get; private set; }
            public int ThumbnailRuns { get; private set; }
            public int ThumbnailStops { get; private set; }

            public double Duration => 10;
            public double FrameRate => 25;
            public IReadOnlyList<double> Keyframes => [0];
            public int AudioStreamCount => streams;
            public bool IsPlaceholder => false;
            public bool IsPlayable => false;
            public string? Activity => null;
            public string? AnalysisError => null;
            public TranscriptState TranscriptState { get; private set; }
            public bool ScenesRequested { get; private set; }
            public bool ThumbnailsRequested { get; private set; }

            public event EventHandler? Changed
            {
                add { }
                remove { }
            }

            public void ScanKeyframes() => KeyframeScans++;

            public void StopKeyframes() => KeyframeStops++;

            public void ReadWaveform()
            {
                WaveformReads++;
                WaveformRequested = true;
            }

            public void StopWaveform()
            {
                WaveformStops++;
                WaveformRequested = false;
            }

            public Task ReadWaveformAsync(CancellationToken cancellationToken)
            {
                WaveformWaits++;
                WaveformRequested = true;
                return Task.CompletedTask;
            }

            public void DetectScenes()
            {
                SceneSearches++;
                ScenesRequested = true;
            }

            public void StopScenes()
            {
                SceneStops++;
                ScenesRequested = false;
            }

            public void ExtractThumbnails()
            {
                ThumbnailRuns++;
                ThumbnailsRequested = true;
            }

            public void StopThumbnails()
            {
                ThumbnailStops++;
                ThumbnailsRequested = false;
            }

            public void StartTranscription(TranscriptionSetup setup)
            {
                Transcriptions++;
                TranscriptState = TranscriptState.Waiting;
            }

            public void StopTranscription()
            {
                TranscriptionStops++;
                TranscriptState = TranscriptState.None;
            }

            public double AudioPeak(int stream, double startTime, double endTime) => 0;

            public void DrawFrame(DrawingContext context, Rect rect, double time, FrameLook look, int variant)
            {
            }
        }
    }
}
