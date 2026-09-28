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
/// The timeline toolbar's chips: kept for every project and every run, and only what they show is worked out (thumbnails
/// while Frames is on, scene changes while Scenes is on, a transcript while Transcript is on).
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
        Assert.True(first.ShowKeyframes && first.ShowSilences && first.SnapToKeyframes);
        Assert.False(first.ShowFrames || first.ShowScenes || first.ShowTranscriptLane);
        Assert.Equal("Show scene changes (finding them reads every frame, so it takes a while)", first.ScenesTip);

        first.ToggleFramesCommand.Execute(null);
        first.ToggleKeyframesCommand.Execute(null);
        first.ToggleScenesCommand.Execute(null);
        first.ToggleSnapCommand.Execute(null);
        first.ToggleTranscriptLaneCommand.Execute(null);
        Assert.Equal("Scene changes: found in every video you open", first.ScenesTip);

        var saved = new AppSettingsStore(SettingsFile).Load();
        Assert.Equal(new TimelineSettings(Keyframes: false, Silences: true, Scenes: true, Snap: false, Frames: true), saved.Timeline);
        Assert.True(saved.Transcription.TranscribeOnOpen);
        var second = Start();
        Assert.True(second.ShowFrames);
        Assert.False(second.ShowKeyframes);
        Assert.True(second.ShowSilences);
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
        Assert.True(editor.ShowKeyframes && editor.ShowSilences && editor.ShowScenes && editor.ShowFrames && editor.SnapToKeyframes);

        editor.ToggleKeyframesCommand.Execute(null);
        Assert.False(File.Exists(SettingsFile));

        // Opening a file leaves the design: the user's own chips (here the defaults) come back.
        await editor.OpenMediaAsync("/videos/talk.mp4");
        Assert.False(editor.IsDemo);
        Assert.True(editor.ShowKeyframes);
        Assert.False(editor.ShowScenes);
        Assert.False(editor.ShowFrames);
        Assert.Equal(0, ((CountingOpener.Preview)editor.Media!).ThumbnailRuns);
        Assert.False(File.Exists(SettingsFile));
    }

    /// <summary>Opens every path as a small file that counts what is asked of it.</summary>
    private sealed class CountingOpener : IMediaOpener
    {
        public Preview? Last { get; private set; }

        public Task<OpenedMedia> OpenAsync(string path, CancellationToken cancellationToken = default)
        {
            Last = new Preview();
            return Task.FromResult(new OpenedMedia(new HighlightCut.Core.Model.SourceMedia(path, 10, 25, [new(1, "Mic")]), Last, "talk.mp4"));
        }

        public sealed class Preview : IMediaPreview
        {
            public int SceneSearches { get; private set; }
            public int SceneStops { get; private set; }
            public int Transcriptions { get; private set; }
            public int TranscriptionStops { get; private set; }
            public int ThumbnailRuns { get; private set; }
            public int ThumbnailStops { get; private set; }

            public double Duration => 10;
            public double FrameRate => 25;
            public IReadOnlyList<double> Keyframes => [0];
            public int AudioStreamCount => 1;
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
