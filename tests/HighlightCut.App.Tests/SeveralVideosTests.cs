using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.Core.Editing;
using HighlightCut.Core.Model;
using HighlightCut.Core.Transcripts;
using HighlightCut.Media.Playback;
using HighlightCut.Transcription;
using HighlightCut.Transcription.Models;

namespace HighlightCut.App.Tests;

/// <summary>
/// A video for the tests: 10 s at 25 fps, keyframes every 2 s, one silence at 4–5 s. A file whose name has "mono" has
/// one audio track, the others two.
/// </summary>
internal sealed class FakeVideo : IMediaPreview
{
    public FakeVideo(string path)
    {
        Path = path;
        AudioStreamCount = path.Contains("mono", StringComparison.Ordinal) ? 1 : 2;
    }

    public string Path { get; }
    public double Duration => 10;
    public double FrameRate => 25;
    public IReadOnlyList<double> Keyframes { get; set; } = [0, 2, 4, 6, 8];
    public IReadOnlyList<TimeRange> Silences => [new(4, 5)];
    public IReadOnlyList<double> SceneChanges => [3];
    public int AudioStreamCount { get; }
    public bool IsPlaceholder => false;
    public string? Activity => null;
    public string? AnalysisError => null;
    public List<string> Asked { get; } = [];

    public Transcript? Transcript { get; set; }
    public TranscriptState TranscriptState { get; set; }

    public void StartTranscription(TranscriptionSetup setup)
    {
        Asked.Add("transcribe");
        TranscriptState = TranscriptState.Running;
    }

    /// <summary>Transcription is through: one word, "word-{name}", at 1–2 s.</summary>
    public void FinishTranscript(string word)
    {
        Transcript = new Transcript("m", "en", [new Word(word, 1, 2)]);
        TranscriptState = TranscriptState.Done;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public HighlightCut.Media.Analysis.SilenceAnalysis? FindSilences(double minDuration, double? thresholdDb, IReadOnlyList<int>? streams) =>
        new(Silences, thresholdDb ?? -40, -60, true);

    public HighlightCut.Media.Analysis.SceneAnalysis? FindSceneChanges(double threshold) => new(SceneChanges, threshold, true, 1);

    public void ScanKeyframes() => Asked.Add("keyframes");
    public void ReadWaveform() => Asked.Add("waveform");
    public void DetectScenes() => Asked.Add("scenes");
    public void ExtractThumbnails() => Asked.Add("thumbnails");

    public event EventHandler? Changed;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public double AudioPeak(int stream, double startTime, double endTime) => 0.5;

    public void DrawFrame(DrawingContext context, Rect rect, double time, FrameLook look, int variant) =>
        context.FillRectangle(Brushes.DimGray, rect);
}

/// <summary>Opens <see cref="FakeVideo"/>s and keeps them by path.</summary>
internal sealed class FakeVideoOpener : IMediaOpener
{
    public Dictionary<string, FakeVideo> Opened { get; } = [];

    public Task<OpenedMedia> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (path.Contains("missing", StringComparison.Ordinal))
            throw new FileNotFoundException("The file does not exist.", path);
        var video = new FakeVideo(path);
        Opened[path] = video;
        AudioTrack[] tracks = video.AudioStreamCount == 1 ? [new(1, "Mic")] : [new(1, "Mic"), new(2, "Game")];
        return Task.FromResult(new OpenedMedia(new SourceMedia(path, 10, 25, [.. tracks]), video, System.IO.Path.GetFileName(path)));
    }
}

public class SeveralVideosTests
{
    private static async Task<(EditorViewModel Editor, FakePlayer Player, FakeVideoOpener Opener)> OpenAsync(params string[] more)
    {
        var player = new FakePlayer();
        var opener = new FakeVideoOpener();
        var editor = App.CreateEditor(null, opener, player: player);
        await editor.OpenMediaAsync("/videos/part1.mp4");
        if (more.Length > 0)
            await editor.AddVideosAsync(more);
        Dispatcher.UIThread.RunJobs();
        return (editor, player, opener);
    }

    // ---- Adding, removing, moving ----------------------------------------------------------------

    [AvaloniaFact]
    public async Task Adding_videos_appends_them_as_one_undo_step()
    {
        var (editor, player, _) = await OpenAsync();
        Assert.False(editor.HasSeveralVideos);

        var ids = await editor.AddVideosAsync(["/videos/part2.mp4", "/videos/part3 mono.mp4"]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([2, 3], ids);
        Assert.True(editor.HasSeveralVideos);
        Assert.Equal(30, editor.Duration);
        Assert.Equal(["part1.mp4", "part2.mp4", "part3 mono.mp4"], editor.Videos.Select(v => v.Name));
        Assert.Equal(["0:00 – 0:10", "0:10 – 0:20", "0:20 – 0:30"], editor.Videos.Select(v => v.PlaceText));
        Assert.StartsWith("edl://", player.LoadedPath, StringComparison.Ordinal);
        Assert.Contains("/videos/part3 mono.mp4,start=0,length=10", player.LoadedPath, StringComparison.Ordinal);
        Assert.Equal([0.0, 10, 20], Assert.IsType<TimelinePreview>(editor.Media).Parts.Select(p => p.Offset));

        editor.Undo();
        Dispatcher.UIThread.RunJobs();
        Assert.False(editor.HasSeveralVideos);
        Assert.Equal(10, editor.Duration);
        Assert.Equal("/videos/part1.mp4", player.LoadedPath);
        Assert.IsType<FakeVideo>(editor.Media);

        editor.Redo();
        Assert.Equal(3, editor.Videos.Count);
    }

    [AvaloniaFact]
    public async Task A_video_that_cannot_be_opened_is_left_out_with_the_reason()
    {
        var (editor, _, _) = await OpenAsync();

        await editor.AddVideosAsync(["/videos/missing.mp4", "/videos/part2.mp4"]);

        Assert.Equal(2, editor.Videos.Count);
        Assert.Contains("Added part2.mp4", editor.StatusMessage, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Removing_a_video_with_clips_asks_first_and_undo_brings_it_back()
    {
        var (editor, _, opener) = await OpenAsync("/videos/part2.mp4");
        editor.KeepRange(12, 14);
        var video = editor.Videos[1];
        Assert.Equal("1 clip", video.ClipsText);

        video.Remove();
        Assert.True(video.IsConfirmingRemove);
        Assert.Equal(2, editor.Session.Project.Sources.Count);

        video.RemoveCommand.Execute(null);
        Assert.Single(editor.Session.Project.Sources);
        Assert.Empty(editor.Clips);
        Assert.False(editor.HasSeveralVideos);

        editor.Undo();
        Assert.Equal(2, editor.Session.Project.Sources.Count);
        Assert.Equal((12.0, 14.0), (editor.Clips[0].Start, editor.Clips[0].End));
        // The video comes back with the preview it had, so nothing is read again.
        Assert.Same(opener.Opened["/videos/part2.mp4"], editor.PreviewOf(2));
    }

    [AvaloniaFact]
    public async Task A_video_without_clips_goes_at_once_and_the_only_one_stays()
    {
        var (editor, _, _) = await OpenAsync("/videos/part2.mp4");

        editor.Videos[1].Remove();
        Assert.Single(editor.Videos);

        editor.RemoveVideo(1);
        Assert.Single(editor.Session.Project.Sources);
        Assert.Contains("only video", editor.StatusMessage, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Moving_a_video_moves_its_clips_along_the_timeline()
    {
        var (editor, _, _) = await OpenAsync("/videos/part2.mp4", "/videos/part3.mp4");
        editor.KeepRange(12, 14);
        var clip = editor.Clips[0];

        editor.Videos[1].MoveDownCommand.Execute(null);

        Assert.Equal(["part1.mp4", "part3.mp4", "part2.mp4"], editor.Videos.Select(v => v.Name));
        Assert.Equal((22.0, 24.0), (clip.Start, clip.End));
        Assert.False(editor.Videos[0].CanMoveUp);
        Assert.False(editor.Videos[2].CanMoveDown);

        editor.Undo();
        Assert.Equal((12.0, 14.0), (clip.Start, clip.End));
    }

    // ---- The timeline ---------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Clips_are_shown_in_timeline_seconds_with_their_video()
    {
        var (editor, _, _) = await OpenAsync("/videos/part2.mp4");

        editor.KeepRange(3, 5);
        editor.KeepRange(13, 15);

        var clips = editor.Session.Project.Clips;
        Assert.Equal([(1, 3.0, 5.0), (2, 3.0, 5.0)], clips.Select(c => (c.SourceId, c.Start, c.End)));
        Assert.Equal([(3.0, 5.0), (13.0, 15.0)], editor.Clips.Select(c => (c.Start, c.End)));
        Assert.Equal(["part1.mp4", "part2.mp4"], editor.Clips.Select(c => c.VideoName));
        Assert.Contains("00:00:13.000", editor.Clips[1].RangeText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task One_video_shows_no_video_names()
    {
        var (editor, _, _) = await OpenAsync();
        editor.KeepRange(3, 5);
        Assert.Null(editor.Clips[0].VideoName);
        Assert.False(editor.Clips[0].HasVideoName);
    }

    [AvaloniaFact]
    public async Task Trim_split_and_mark_in_work_in_timeline_seconds()
    {
        var (editor, _, _) = await OpenAsync("/videos/part2.mp4");
        editor.KeepRange(12, 16);
        var clip = editor.Clips[0];

        editor.Trim(clip, inPoint: true, 11);
        Assert.Equal(11, clip.Start);
        Assert.Equal(1, editor.Session.Project.Clips[0].Start);

        // The magnet: the in-point stops at the start of its video, not in the video before.
        editor.Trim(clip, inPoint: true, 8);
        Assert.Equal(10, clip.Start);

        editor.SetTime(13);
        editor.Split();
        Assert.Equal([(10.0, 13.0), (13.0, 16.0)], editor.Clips.Select(c => (c.Start, c.End)));

        editor.Select(null);
        editor.SetTime(18);
        editor.MarkIn();
        Assert.Equal((18.0, 20.0), (editor.SelectedClip!.Start, editor.SelectedClip.End));
        Assert.Equal(2, editor.SelectedClip.SourceId);
    }

    [AvaloniaFact]
    public async Task Keeping_a_range_over_a_join_makes_a_clip_in_each_video()
    {
        var (editor, _, _) = await OpenAsync("/videos/part2.mp4");

        editor.KeepRange(8, 12);

        Assert.Equal([(8.0, 10.0, 1), (10.0, 12.0, 2)], editor.Clips.Select(c => (c.Start, c.End, c.SourceId)));
        editor.Undo();
        Assert.Empty(editor.Clips);
    }

    // ---- The player ------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Seeking_and_playing_run_across_the_joins()
    {
        var (editor, player, _) = await OpenAsync("/videos/part2.mp4");
        player.Calls.Clear();

        editor.SetTime(15);
        Assert.Equal(["seek 15"], player.Calls);

        player.Report(19.96, playing: true);
        Assert.Equal(19.96, editor.Time, 6);
        Assert.Equal(20, editor.Duration);

        editor.KeepRange(12, 14);
        editor.SelectFromList(editor.Clips[0]);
        Assert.Equal(12, editor.Time);
    }

    [AvaloniaFact]
    public async Task The_lanes_follow_the_video_under_the_playhead()
    {
        var (editor, player, _) = await OpenAsync("/videos/part2 mono.mp4");
        editor.VolumeApplyDelay = TimeSpan.Zero;
        Assert.Equal(["A1", "A2"], editor.AudioLanes.Select(l => l.Key));
        Assert.All(editor.AudioLanes, l => Assert.True(l.HasTrack));
        editor.AudioLanes[1].IsMuted = true;
        player.Calls.Clear();

        player.Report(12, playing: true);

        Assert.Equal(2, editor.AudioLanes[0].SourceId);
        Assert.False(editor.AudioLanes[1].HasTrack);
        Assert.Equal("part2 mono.mp4", editor.AudioLanes[0].VideoName);
        // The mono video's own mix for track 1 is the same, and track 2 keeps what it had (muted): mpv's mix stays as it is.
        Assert.Empty(player.Calls);

        editor.AudioLanes[0].GainDb = -6;
        Assert.Equal(["tracks 10 gains -6,0"], player.Calls);
        Assert.Equal(-6, editor.Session.Project.MixOf(2, 1).GainDb);
        Assert.Equal(0, editor.Session.Project.MixOf(1, 1).GainDb);

        player.Report(3);
        Assert.Equal(1, editor.AudioLanes[0].SourceId);
        Assert.Equal(0, editor.AudioLanes[0].GainDb);
        Assert.True(editor.AudioLanes[1].IsMuted);
    }

    [AvaloniaFact]
    public async Task Moving_a_video_reloads_the_player_where_the_playhead_is()
    {
        var (editor, player, _) = await OpenAsync("/videos/part2.mp4");
        editor.SetTime(4);
        player.Calls.Clear();

        editor.MoveVideo(2, 0);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(player.Calls, c => c.StartsWith("load ", StringComparison.Ordinal));
        Assert.Contains("seek 4", player.Calls);
        Assert.True(player.LoadedPath!.IndexOf("part2", StringComparison.Ordinal) < player.LoadedPath.IndexOf("part1", StringComparison.Ordinal));
    }

    // ---- Analyses per video ----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Each_videos_analyses_are_placed_at_its_offset()
    {
        var (editor, _, _) = await OpenAsync("/videos/part2.mp4");
        var media = editor.Media!;

        Assert.Equal([0.0, 2, 4, 6, 8, 10, 12, 14, 16, 18], media.Keyframes);
        Assert.Equal(media.Keyframes, editor.Session.Keyframes);
        Assert.Equal([new TimeRange(4, 5), new TimeRange(14, 15)], media.Silences);
        Assert.Equal([3.0, 13], media.SceneChanges);

        editor.MoveVideo(2, 0);
        Assert.Equal(editor.Media!.Keyframes, editor.Session.Keyframes);
    }

    [AvaloniaFact]
    public async Task Snapping_uses_the_keyframes_of_every_video()
    {
        var (editor, _, opener) = await OpenAsync("/videos/part2.mp4");
        opener.Opened["/videos/part2.mp4"].Keyframes = [0, 3.5];
        opener.Opened["/videos/part2.mp4"].RaiseChanged();
        Dispatcher.UIThread.RunJobs();
        editor.KeepRange(11, 16);

        editor.Trim(editor.Clips[0], inPoint: true, 13.4, snapThreshold: 0.2);

        Assert.Equal(13.5, editor.Clips[0].Start, 6);
    }

    [AvaloniaFact]
    public async Task Adding_a_video_reads_only_what_the_chips_ask_for()
    {
        var (editor, _, opener) = await OpenAsync();
        editor.ShowKeyframes = false;
        editor.ShowScenes = false;
        editor.ShowFrames = false;
        editor.ShowWaveform = true;

        await editor.AddVideosAsync(["/videos/part2.mp4"]);

        Assert.Equal(["waveform"], opener.Opened["/videos/part2.mp4"].Asked.Distinct());
    }

    [AvaloniaFact]
    public async Task Claude_reads_every_videos_analyses_in_timeline_seconds()
    {
        var (editor, _, opener) = await OpenAsync("/videos/part2.mp4");
        var host = new EditorMcpHost(editor);

        Assert.Equal([0.0, 2, 4, 6, 8, 10, 12, 14, 16, 18], await host.ReadKeyframesAsync(TestContext.Current.CancellationToken));
        Assert.Equal([new TimeRange(4, 5), new TimeRange(14, 15)], host.FindSilences(1, null, null)!.Ranges);
        Assert.Equal([3.0, 13], host.FindSceneChanges(10)!.Times);

        opener.Opened["/videos/part1.mp4"].FinishTranscript("one");
        opener.Opened["/videos/part2.mp4"].FinishTranscript("two");
        var status = host.TranscriptStatus;
        Assert.Equal("done", status.State);
        Assert.Equal([("one", 1.0), ("two", 11.0)], status.Transcript!.Words.Select(w => (w.Text, w.Start)));
    }

    [AvaloniaFact]
    public async Task The_transcript_tab_starts_each_videos_words_under_its_name()
    {
        var (editor, _, opener) = await OpenAsync("/videos/part2.mp4");
        opener.Opened["/videos/part1.mp4"].FinishTranscript("one");
        opener.Opened["/videos/part2.mp4"].FinishTranscript("two");
        Dispatcher.UIThread.RunJobs();

        var paragraphs = editor.TranscriptPanel.Paragraphs;
        Assert.Equal(["1 · part1.mp4", "2 · part2.mp4"], paragraphs.Select(p => p.Video));
        Assert.Equal([1.0, 11.0], paragraphs.Select(p => p.Start));
    }

    [Fact]
    public void Transcription_runs_the_videos_one_after_another_in_timeline_order()
    {
        var first = new FakeVideo("a.mp4");
        var second = new FakeVideo("b.mp4");
        using var timeline = new TimelinePreview([new PreviewPart(1, "a.mp4", 0, 10, first), new PreviewPart(2, "b.mp4", 10, 10, second)]);
        var setup = new TranscriptionSetup(ModelCatalog.Parakeet, "/models", "en");

        timeline.StartTranscription(setup);
        Assert.Equal(["transcribe"], first.Asked);
        Assert.Empty(second.Asked);
        Assert.Equal(TranscriptState.Running, timeline.TranscriptState);

        first.FinishTranscript("one");
        Assert.Equal(["transcribe"], second.Asked);
        Assert.Equal(["one"], timeline.Transcript!.Words.Select(w => w.Text));

        second.FinishTranscript("two");
        Assert.Equal(TranscriptState.Done, timeline.TranscriptState);
        Assert.Equal([("one", 1.0), ("two", 11.0)], timeline.Transcript!.Words.Select(w => (w.Text, w.Start)));
        Assert.Equal(1, timeline.TranscriptProgress, 6);
    }

    [Fact]
    public void A_track_only_some_videos_have_is_read_where_they_have_it()
    {
        var timeline = new TimelinePreview([new PreviewPart(1, "a.mp4", 0, 10, new FakeVideo("a.mp4")),
            new PreviewPart(2, "b mono.mp4", 10, 10, new FakeVideo("b mono.mp4"))]);
        Assert.Equal(2, timeline.AudioStreamCount);
        Assert.Equal(0.5, timeline.AudioPeak(1, 2, 3));
        Assert.Equal(0, timeline.AudioPeak(1, 12, 13));
        Assert.Equal(0.5, timeline.AudioPeak(0, 12, 13));
        Assert.Equal("b mono.mp4", timeline.PartAt(10).Name);
        Assert.Equal("a.mp4", timeline.PartAt(9.99).Name);
    }

    [Fact]
    public void The_player_gets_one_file_or_all_of_them_end_to_end()
    {
        Assert.Equal("/v/a.mp4", MpvTimeline.PathFor([new TimelineFile("/v/a.mp4", 10, 1)]));
        Assert.Equal("edl://%8%/v/a.mp4,start=0,length=10;%11%/v/b, c.mkv,start=0,length=4.5,layout=this",
            MpvTimeline.PathFor([new TimelineFile("/v/a.mp4", 10, 1), new TimelineFile("/v/b, c.mkv", 4.5, 2)]));
        Assert.Equal("edl://%8%/v/a.mp4,start=0,length=10;%8%/v/b.mp4,start=0,length=5",
            MpvTimeline.PathFor([new TimelineFile("/v/a.mp4", 10, 2), new TimelineFile("/v/b.mp4", 5, 2)]));
    }
}
