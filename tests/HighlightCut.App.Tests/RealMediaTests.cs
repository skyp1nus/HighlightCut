using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;
using HighlightCut.Core.Transcripts;
using HighlightCut.Media;
using HighlightCut.Media.Caching;
using HighlightCut.Media.Probing;
using HighlightCut.Media.Tools;
using HighlightCut.Transcription;
using HighlightCut.Transcription.Models;

namespace HighlightCut.App.Tests;

/// <summary>
/// The whole path with ffmpeg: open a generated video, read what the chips (or a feature) ask for, mark clips and
/// export them. Skipped when ffmpeg is not installed.
/// </summary>
public sealed class RealMediaTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-real").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Temp folder; the OS cleans it up.
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>6 s, 320×180, keyframe every second, two named audio streams.</summary>
    private async Task<string> SampleAsync()
    {
        Assert.SkipWhen(NativeTools.FindTool("ffmpeg") is null || NativeTools.FindTool("ffprobe") is null,
            "ffmpeg/ffprobe are not installed.");
        string path = Path.Combine(_dir, "sample.mp4");
        await Task.Run(() => ToolProcess.RunAsync("ffmpeg",
        [
            "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-f", "lavfi", "-i", "sine=frequency=440",
            "-f", "lavfi", "-i", "sine=frequency=880", "-t", "6", "-map", "0:v", "-map", "1:a", "-map", "2:a",
            "-c:v", "libx264", "-preset", "veryfast", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0", "-bf", "2",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-metadata:s:a:0", "handler_name=Mic", "-metadata:s:a:1", "handler_name=Music",
            "-y", path,
        ], null, Ct), Ct);
        return path;
    }

    private async Task<(EditorViewModel Editor, MainWindow Window)> OpenAsync(string video, Action<EditorViewModel>? prepare = null)
    {
        var editor = App.CreateEditor(null, new FfmpegMediaOpener(new MediaCache(Path.Combine(_dir, "cache"))),
            new RecentFilesStore(Path.Combine(_dir, "recent.json")));
        // No transcription unless a test asks for it (the default models folder may have real models).
        editor.Settings.ModelsFolder = Directory.CreateDirectory(Path.Combine(_dir, "no-models")).FullName;
        prepare?.Invoke(editor);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        await editor.OpenMediaAsync(video);
        Assert.True(editor.HasFile, editor.StatusMessage);
        var preview = Assert.IsType<MediaPreview>(editor.Media);
        await preview.Analysis.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        // The editor hears about the finished analysis through the preview's (throttled) Changed event.
        await PumpUntil(() => !editor.StatusRight.Contains("analysing", StringComparison.Ordinal)
                              && (!editor.ShowKeyframes || editor.Session.Keyframes.Count > 0));
        return (editor, window);
    }

    private static async Task PumpUntil(Func<bool> done, int tries = 200)
    {
        for (int i = 0; i < tries && !done(); i++)
        {
            await Task.Delay(25, Ct);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(done());
    }

    /// <summary>Clips [1.5, 3.2] and [4, 5.5], made with the I and O keys' commands.</summary>
    private static void MarkTwoClips(EditorViewModel editor)
    {
        editor.SetTime(1.5);
        editor.MarkIn();
        editor.SetTime(3.2);
        editor.MarkOut();
        editor.Select(null);
        editor.SetTime(4);
        editor.MarkIn();
        editor.SetTime(5.5);
        editor.MarkOut();
        Assert.Equal([(1.5, 3.2), (4.0, 5.5)], editor.Clips.Select(c => (c.Start, c.End)));
    }

    /// <summary>The chips that read something from the file, all on (off by default).</summary>
    private static void AllChips(EditorViewModel e) => e.ShowKeyframes = e.ShowWaveform = e.ShowSilences = e.ShowFrames = true;

    [AvaloniaFact]
    public async Task Opening_a_video_fills_the_timeline_from_ffmpeg()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video, AllChips);
        var preview = (MediaPreview)editor.Media!;

        Assert.Equal("sample", editor.ProjectName);
        Assert.Equal("sample.mp4 · 320×180 · 30 fps", editor.MediaInfo);
        Assert.Equal(["Mic", "Music"], editor.AudioLanes.Select(l => l.Label));
        Assert.Equal(6, preview.Keyframes.Count);
        Assert.Equal(preview.Keyframes, editor.Session.Keyframes);
        Assert.Equal(3, preview.ThumbnailCount);
        Assert.True(preview.Waveform.IsComplete);
        Assert.True(preview.AudioPeak(0, 1, 2) > 0.5);
        Assert.Null(preview.AnalysisError);
        Assert.Equal(video, Assert.Single(editor.RecentFiles).Path);
        Assert.Matches(@"^keyframes \d+\.\d s · thumbnails \d+\.\d s · waveform \d+\.\d s$", preview.AnalysisTimes);
        Assert.Contains("Analysis keyframes ", editor.Settings.DiagnosticsText(), StringComparison.Ordinal);
        // The processing screen (if the analysis took long enough to show it) is gone.
        Assert.False(preview.IsAnalysing);
        Assert.Null(preview.AnalysisStage);
        Assert.Equal(1, preview.AnalysisProgress);
        await PumpUntil(() => !editor.Processing.IsVisible);
        // Nothing that reads the whole video starts by itself.
        Assert.False(preview.ScenesRequested);
        Assert.Equal(TranscriptState.None, preview.TranscriptState);
        Assert.Null(preview.Activity);
        // Claude asking for scene changes starts the detection; it says to come back until it is done.
        var tools = new HighlightCut.Mcp.EditorTools(new EditorMcpHost(editor));
        var scenes = await tools.FindSceneChanges();
        Assert.False(scenes.Complete);
        Assert.StartsWith("Scene detection is 0% done", scenes.Note, StringComparison.Ordinal);
        await preview.ScenesTask.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.True((await tools.FindSceneChanges()).Complete);

        MarkTwoClips(editor);
        editor.ZoomLevel = 0.3;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(Screenshots.Directory, "real-media.png"), new PngBitmapEncoderOptions());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Silences_and_scene_changes_appear_on_the_timeline_and_Claude_can_cut_the_pauses()
    {
        // Cuts at 3, 6 and 9 s with steady motion in between; a tone with pauses at 2–4 s and 7–8.5 s.
        _ = await SampleAsync();
        string video = Path.Combine(_dir, "talk.mp4");
        const string Graph =
            "testsrc2=size=320x180:rate=30:duration=3[v0];smptehdbars=size=320x180:rate=30:duration=3[v1];" +
            "mandelbrot=size=320x180:rate=30,trim=duration=3[v2];testsrc=size=320x180:rate=30:duration=3[v3];" +
            "[v0][v1][v2][v3]concat=n=4:v=1:a=0,format=yuv420p[v];" +
            "sine=frequency=440:duration=2[a0];aevalsrc=0:d=2[a1];sine=frequency=440:duration=3[a2];aevalsrc=0:d=1.5[a3];" +
            "sine=frequency=440:duration=3.5[a4];[a0][a1][a2][a3][a4]concat=n=5:v=0:a=1[a]";
        await Task.Run(() => ToolProcess.RunAsync("ffmpeg",
        [
            "-v", "error", "-filter_complex", Graph, "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-preset", "veryfast",
            "-g", "30", "-c:a", "aac", "-y", video,
        ], null, Ct), Ct);

        var (editor, window) = await OpenAsync(video, e => e.ShowSilences = true);
        var preview = (MediaPreview)editor.Media!;
        // Scene detection waits for the Scenes chip (or Claude).
        Assert.False(editor.HasSceneData);
        Assert.False(preview.ScenesRequested);
        Assert.Equal("Show scene changes (finding them reads every frame, so it takes a while)", editor.ScenesTip);
        Assert.True(editor.CanToggleScenes);
        Assert.False(editor.ScenesOn);

        editor.ToggleScenesCommand.Execute(null);
        Assert.True(editor.ScenesOn);
        await preview.ScenesTask.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await PumpUntil(() => editor.HasSceneData && editor.HasSilenceData && editor.Media!.ScenesComplete);

        Assert.Equal("Silence bands: 2 pauses of a second or more", editor.SilenceTip);
        Assert.Equal("Scene changes: 3", editor.ScenesTip);
        Assert.Matches(@" · scenes \d+\.\d s$", preview.AnalysisTimes);
        editor.ToggleScenesCommand.Execute(null);
        Assert.False(editor.ScenesOn);
        var silences = editor.Media!.Silences;
        Assert.Equal([2.0, 7.0], silences.Select(r => Math.Round(r.Start)));
        Assert.Equal([3.0, 6.0, 9.0], editor.Media.SceneChanges.Select(t => Math.Round(t, 1)));

        // Claude's view of the same analysis, and its cut: the whole video minus the pauses, as one edit.
        var tools = new HighlightCut.Mcp.EditorTools(new EditorMcpHost(editor));
        var scenes = await tools.FindSceneChanges(threshold: 20);
        Assert.Equal(3, scenes.Count);
        var cut = await tools.CutSilences(padding: 0.1);
        Assert.Equal(3, editor.Clips.Count);
        Assert.StartsWith("Removed 2 silences", cut.Result, StringComparison.Ordinal);
        Assert.True(editor.Clips.All(c => c.IsAiChanged));
        // 3.5 s of pauses, less 0.1 s kept at each of their four edges.
        Assert.InRange(editor.OutputDuration, editor.Duration - 3.1 - 0.2, editor.Duration - 3.1 + 0.2);

        editor.ZoomLevel = 0;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(Screenshots.Directory, "silences-scenes.png"), new PngBitmapEncoderOptions());
        window.Close();
    }

    /// <summary>Says "word1", "word2"… one per piece, and counts the pieces.</summary>
    private sealed class FakeRecognizer : ISpeechRecognizer
    {
        public static int Pieces;

        public IReadOnlyList<Word> Recognize(float[] samples, double offset)
        {
            int n = Interlocked.Increment(ref Pieces);
            return [new Word($"word{n}", offset + 0.5, offset + 1)];
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A models folder where the model counts as installed (its files exist, empty).</summary>
    private string FakeModels(TranscriptionModel model)
    {
        string models = Path.Combine(_dir, "models");
        string dir = Directory.CreateDirectory(Path.Combine(models, model.Id)).FullName;
        foreach (string f in model.Files)
            File.WriteAllText(Path.Combine(dir, f), "");
        return models;
    }

    [AvaloniaFact]
    public async Task A_video_is_transcribed_after_the_analysis_and_the_transcript_is_cached()
    {
        string video = await SampleAsync();
        string models = FakeModels(ModelCatalog.Parakeet);
        FakeRecognizer.Pieces = 0;
        void Prepare(EditorViewModel e)
        {
            e.Settings.TranscribeOnOpen = true;
            e.Settings.ModelsFolder = models;
            e.RecognizerFactory = _ => new FakeRecognizer();
        }

        var (editor, window) = await OpenAsync(video, Prepare);
        await PumpUntil(() => editor.Media!.TranscriptState == TranscriptState.Done);

        var transcript = editor.Media!.Transcript!;
        Assert.Equal("parakeet-tdt-0.6b-v3", transcript.Model);
        Assert.Equal(["word1"], transcript.Words.Select(w => w.Text));
        Assert.Equal(1, FakeRecognizer.Pieces);

        // Claude reads it through the editor's MCP host, and can cut by it.
        var tools = new HighlightCut.Mcp.EditorTools(new EditorMcpHost(editor));
        var read = await tools.GetTranscript();
        Assert.Equal("done", read.Status);
        Assert.Equal(["00:00.500–00:01.000 word1"], read.Lines);
        Assert.Equal(1, (await tools.SearchTranscript("WORD1")).Count);
        var cut = await tools.CutRanges([new HighlightCut.Mcp.CutRange(0.5, 1.0)], "Removed word1");
        Assert.Equal(2, editor.Clips.Count);
        Assert.StartsWith("Removed word1", cut.Result, StringComparison.Ordinal);
        window.Close();

        // Opened again, with "transcribe when opened" off: shown from the cache, not recognized again.
        var (again, w2) = await OpenAsync(video, e =>
        {
            Prepare(e);
            e.Settings.TranscribeOnOpen = false;
        });
        await PumpUntil(() => again.Media!.TranscriptState == TranscriptState.Done);
        Assert.Equal(["word1"], again.Media!.Transcript!.Words.Select(w => w.Text));
        Assert.Equal(1, FakeRecognizer.Pieces);
        w2.Close();
    }

    [AvaloniaFact]
    public async Task Without_a_model_there_is_no_transcription()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        Assert.Contains("No transcription model", editor.StartTranscription(), StringComparison.Ordinal);
        Assert.Equal(TranscriptState.None, editor.Media!.TranscriptState);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Parakeet_transcribes_a_real_video()
    {
        string? models = Environment.GetEnvironmentVariable("HIGHLIGHTCUT_MODELS_DIR");
        Assert.SkipUnless(models is { Length: > 0 } && new ModelStore(models).IsInstalled(ModelCatalog.Parakeet),
            "Parakeet is not installed in HIGHLIGHTCUT_MODELS_DIR.");
        await SampleAsync();
        string speech = Path.Combine(new ModelStore(models!).DirectoryOf(ModelCatalog.Parakeet), "test_wavs", "en.wav");
        string video = Path.Combine(_dir, "speech.mp4");
        await Task.Run(() => ToolProcess.RunAsync("ffmpeg",
        [
            "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-i", speech, "-af", "adelay=2000,apad=pad_dur=2",
            "-shortest", "-c:v", "libx264", "-preset", "veryfast", "-c:a", "aac", "-y", video,
        ], null, Ct), Ct);

        var (editor, window) = await OpenAsync(video, e =>
        {
            e.Settings.TranscribeOnOpen = true;
            e.Settings.ModelsFolder = models!;
        });
        await PumpUntil(() => editor.Media!.TranscriptState is TranscriptState.Done or TranscriptState.Failed, 1500);

        var transcript = editor.Media!.Transcript!;
        Assert.Contains("ask not what your country can do for you", transcript.Text, StringComparison.OrdinalIgnoreCase);
        // The speech starts 2 s in.
        Assert.Equal(2, transcript.Words[0].Start, 0.4);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_second_open_reads_the_analysis_from_the_cache()
    {
        string video = await SampleAsync();
        var (first, w1) = await OpenAsync(video, AllChips);
        var a = (MediaPreview)first.Media!;
        a.DetectScenes();
        await a.ScenesTask.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        w1.Close();
        var (second, w2) = await OpenAsync(video, AllChips);
        var b = (MediaPreview)second.Media!;
        Assert.Equal(a.Keyframes, b.Keyframes);
        Assert.Equal(a.ThumbnailCount, b.ThumbnailCount);
        Assert.Equal(a.Waveform.Filled, b.Waveform.Filled);
        // Scene changes found before are read back too; the chip (off at first) only decides whether they are shown.
        Assert.Equal("keyframes cached · thumbnails cached · waveform cached · scenes cached", b.AnalysisTimes);
        Assert.True(b.ScenesRequested && b.ScenesComplete);
        Assert.False(second.ScenesOn);
        second.ToggleScenesCommand.Execute(null);
        Assert.True(second.ScenesOn);
        // The sample is one shot: the cached search found nothing, and nothing is searched again.
        Assert.Equal("No scene changes found", second.ScenesTip);
        // Turning the chip off keeps what was found.
        second.ToggleScenesCommand.Execute(null);
        Assert.True(b.ScenesComplete);
        w2.Close();
    }

    [AvaloniaFact]
    public async Task Thumbnails_are_made_only_while_the_Frames_chip_is_on()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        var preview = (MediaPreview)editor.Media!;

        // Off (the default): none are made.
        Assert.False(editor.ShowFrames);
        Assert.False(preview.ThumbnailsRequested);
        Assert.Equal(0, preview.ThumbnailCount);
        Assert.Null(preview.AnalysisTimes);

        // On: the open file's are made, without the processing screen coming back.
        editor.ToggleFramesCommand.Execute(null);
        Assert.True(preview.ThumbnailsRequested);
        Assert.False(preview.IsAnalysing);
        Assert.StartsWith("making thumbnails ", preview.Activity, StringComparison.Ordinal);
        await preview.ThumbnailsTask.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(3, preview.ThumbnailCount);
        Assert.Null(preview.Activity);
        Assert.Matches(@"^thumbnails \d+\.\d s$", preview.AnalysisTimes);

        // Off keeps a finished set, so on again shows it at once without making it again.
        editor.ToggleFramesCommand.Execute(null);
        Assert.Equal(3, preview.ThumbnailCount);
        editor.ToggleFramesCommand.Execute(null);
        Assert.True(preview.ThumbnailsTask.IsCompleted);
        Assert.Equal(3, preview.ThumbnailCount);
        window.Close();

        // The next open with the chip on reads them from the cache.
        var (again, w2) = await OpenAsync(video, e => e.ShowFrames = true);
        var b = (MediaPreview)again.Media!;
        Assert.Equal(3, b.ThumbnailCount);
        Assert.Contains("thumbnails cached", b.AnalysisTimes, StringComparison.Ordinal);
        w2.Close();
    }

    [AvaloniaFact]
    public async Task Turning_the_Frames_chip_off_stops_the_thumbnails_and_keeps_nothing_of_them()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        var preview = (MediaPreview)editor.Media!;
        editor.ToggleFramesCommand.Execute(null);
        Assert.True(preview.ThumbnailsRequested);

        editor.ToggleFramesCommand.Execute(null);
        await preview.ThumbnailsTask.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.False(preview.ThumbnailsRequested);
        Assert.Equal(0, preview.ThumbnailCount);
        Assert.Null(preview.Activity);
        Assert.DoesNotContain("thumbnails", preview.AnalysisTimes ?? "", StringComparison.Ordinal);

        // Nothing half done was cached: turned on again, they are made afresh.
        editor.ToggleFramesCommand.Execute(null);
        await preview.ThumbnailsTask.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(3, preview.ThumbnailCount);
        Assert.Matches(@"thumbnails \d+\.\d s", preview.AnalysisTimes);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Turning_the_Scenes_chip_off_stops_the_search_and_keeps_nothing_of_it()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        var preview = (MediaPreview)editor.Media!;
        editor.ToggleScenesCommand.Execute(null);
        Assert.True(preview.ScenesRequested);

        editor.ToggleScenesCommand.Execute(null);
        await preview.ScenesTask.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.False(preview.ScenesRequested);
        Assert.Empty(preview.SceneChanges);
        Assert.DoesNotContain("scenes", preview.AnalysisTimes ?? "", StringComparison.Ordinal);
        window.Close();

        // Nothing half done was cached: the next open has no scene changes until the chip asks.
        var (again, w2) = await OpenAsync(video);
        var b = (MediaPreview)again.Media!;
        Assert.False(b.ScenesRequested);
        Assert.DoesNotContain("scenes", b.AnalysisTimes ?? "", StringComparison.Ordinal);
        w2.Close();
    }

    [AvaloniaFact]
    public async Task Opening_a_video_reads_nothing_until_a_chip_asks()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        var preview = (MediaPreview)editor.Media!;

        // The chips that read the file are off by default: nothing is scanned or read, and no processing screen shows.
        Assert.False(editor.ShowKeyframes || editor.ShowWaveform || editor.ShowSilences || editor.ShowFrames || editor.ShowScenes);
        Assert.True(preview.Analysis.IsCompleted);
        Assert.False(preview.IsAnalysing);
        Assert.False(editor.Processing.IsVisible);
        Assert.Empty(preview.Keyframes);
        Assert.Equal(0, preview.Waveform.Decoded);
        Assert.Null(preview.AnalysisTimes);
        Assert.Null(preview.Activity);
        Assert.Contains("Timeline chips keyframes off · waveform off · silence off · scenes off · frames off",
            editor.Settings.DiagnosticsText(), StringComparison.Ordinal);
        Assert.StartsWith("Snap trims to keyframes (turn on Keyframes to find them).", editor.SnapTip, StringComparison.Ordinal);

        // Keyframes on: scanned, shown and snapped to, without the processing screen.
        editor.ToggleKeyframesCommand.Execute(null);
        Assert.False(preview.IsAnalysing);
        Assert.StartsWith("finding keyframes ", preview.Activity, StringComparison.Ordinal);
        await preview.KeyframesTask.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await PumpUntil(() => editor.Session.Keyframes.Count == 6);
        Assert.Matches(@"^keyframes \d+\.\d s$", preview.AnalysisTimes);
        Assert.Equal("Keyframes: 6. A lossless export starts each clip on one; trims snap to them", editor.KeyframesTip);

        // Waveform on: all the audio is read; Silence on as well reads nothing more.
        editor.ToggleWaveformCommand.Execute(null);
        editor.ToggleSilencesCommand.Execute(null);
        await preview.WaveformTask.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await PumpUntil(() => preview.Waveform.IsComplete && editor.SilenceTip == "No pauses of a second or more in this file");
        Assert.Matches(@"^keyframes \d+\.\d s · waveform \d+\.\d s$", preview.AnalysisTimes);
        Assert.False(editor.Processing.IsVisible);

        // Off keeps what was read.
        editor.ToggleKeyframesCommand.Execute(null);
        editor.ToggleWaveformCommand.Execute(null);
        editor.ToggleSilencesCommand.Execute(null);
        Assert.Equal(6, preview.Keyframes.Count);
        Assert.True(preview.Waveform.IsComplete);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Turning_the_Keyframes_and_Waveform_chips_off_stops_the_reads_and_keeps_nothing_of_them()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        var preview = (MediaPreview)editor.Media!;

        editor.ToggleKeyframesCommand.Execute(null);
        editor.ToggleWaveformCommand.Execute(null);
        Assert.True(preview.KeyframesRequested && preview.WaveformRequested);
        editor.ToggleKeyframesCommand.Execute(null);
        editor.ToggleWaveformCommand.Execute(null);
        await Task.WhenAll(preview.KeyframesTask, preview.WaveformTask).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await PumpUntil(() => preview.Activity is null);

        Assert.False(preview.KeyframesRequested || preview.WaveformRequested);
        Assert.Empty(preview.Keyframes);
        Assert.Equal(0, preview.Waveform.Decoded);
        Assert.Null(preview.AnalysisTimes);

        // Nothing half done was cached: on again, they are read afresh. The Silence chip alone reads the waveform too.
        editor.ToggleKeyframesCommand.Execute(null);
        editor.ToggleSilencesCommand.Execute(null);
        await preview.KeyframesTask.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await PumpUntil(() => preview.Waveform.IsComplete && preview.Activity is null);
        Assert.Equal(6, preview.Keyframes.Count);
        Assert.Matches(@"^keyframes \d+\.\d s · waveform \d+\.\d s$", preview.AnalysisTimes);
        window.Close();

        // Opened again with the chips on, both come from the cache, as the file opens.
        var (again, w2) = await OpenAsync(video, e => e.ShowKeyframes = e.ShowWaveform = true);
        var b = (MediaPreview)again.Media!;
        Assert.Equal("keyframes cached · waveform cached", b.AnalysisTimes);
        Assert.Equal(6, again.Session.Keyframes.Count);
        w2.Close();
    }

    [AvaloniaFact]
    public async Task Features_that_need_keyframes_or_the_audio_read_them_when_asked()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        var preview = (MediaPreview)editor.Media!;
        var tools = new HighlightCut.Mcp.EditorTools(new EditorMcpHost(editor));
        Assert.Null(preview.AnalysisTimes);

        // Claude's keyframe tool scans them (and they stay, for snapping).
        var keyframes = await tools.FindKeyframes(0, 10);
        Assert.Equal([0.0, 1.0, 2.0, 3.0, 4.0, 5.0], keyframes.Select(t => Math.Round(t)));
        Assert.Equal(6, preview.Keyframes.Count);
        Assert.False(editor.ShowKeyframes);

        // Claude's silence tool reads the whole of the audio first: its answer is complete.
        var silences = await tools.FindSilences();
        Assert.True(silences.Complete);
        Assert.True(preview.Waveform.IsComplete);
        // The waveform stays hidden while its chip is off.
        Assert.False(editor.ShowWaveform);
        window.Close();

        // Evening out the volumes reads the audio of a file opened without it.
        var (other, w2) = await OpenAsync(video);
        var b = (MediaPreview)other.Media!;
        Assert.Equal(0, b.Waveform.Decoded);
        other.AudioLanes[1].GainDb = -6;
        await other.EvenOutVolumesCommand.ExecuteAsync(null);
        Assert.True(b.Waveform.IsComplete);
        Assert.StartsWith("Evened out: ", other.StatusMessage, StringComparison.Ordinal);
        w2.Close();
    }

    [AvaloniaFact]
    public async Task Exporting_writes_the_kept_clips_without_muted_tracks()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        editor.AudioLanes[0].IsMuted = true;
        string outDir = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;
        var revealed = new List<string>();
        editor.RevealInFolder = revealed.Add;

        var export = editor.Export;
        export.Open();
        Assert.Equal(_dir, export.OutputFolder);
        Assert.Equal("MP4", export.Container);
        Assert.Equal("Copy (AAC 44.1 kHz)", export.Audio.Label);
        export.OutputFolder = outDir;
        export.KeepAllTracks = false;
        // The Keyframes chip is off: the lossless export finds them itself, and says so meanwhile.
        Assert.Empty(editor.Media!.Keyframes);
        var started = export.StartAsync();
        Assert.True(export.IsPreparing);
        Assert.Equal("Finding keyframes…", export.ProgressText);
        Assert.EndsWith("a lossless cut starts each clip on a keyframe", export.Stats, StringComparison.Ordinal);
        await started;
        Assert.Equal(6, editor.Media.Keyframes.Count);

        Assert.True(export.IsDone, export.ErrorText);
        Assert.Equal("Export complete", export.Title);
        Assert.Equal(["1 · Clip 1", "2 · Clip 2", "Merge into sample-cut.mp4"], export.Rows.Select(r => r.Name));
        Assert.All(export.Rows, r => Assert.True(r.IsDone));
        string output = Path.Combine(outDir, "sample-cut.mp4");
        Assert.Equal([output], Directory.GetFiles(outDir));
        Assert.Equal("Exported sample-cut.mp4", editor.StatusMessage);
        // Settings → Export → After export: Show in folder.
        Assert.Equal([output], revealed);

        var result = await MediaProbe.ProbeAsync(output, Ct);
        Assert.Equal(["Music"], result.Audio.Select(a => a.Label));
        // Lossless: 1.0–3.2 and 4.0–5.5 (the in-points move back to keyframes), plus a few frames.
        Assert.InRange(result.Duration, 3.65, 4.3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Claude_exports_in_the_background_and_never_overwrites()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        string outDir = Path.Combine(_dir, "by-claude");
        var tools = new HighlightCut.Mcp.EditorTools(new EditorMcpHost(editor));
        var revealed = new List<string>();
        editor.RevealInFolder = revealed.Add;
        // Claude's exports never replace a file, whatever the user's own exports do.
        editor.Settings.IfFileExists = FileExistsAction.Overwrite;

        // Export: Ask (the default) shows the request; "Always allow" lets this and later exports run.
        var asked = tools.Export(container: "mkv", folder: outDir);
        await PumpUntil(() => editor.Claude.Export.IsAsking);
        Assert.False(asked.IsCompleted);
        Assert.StartsWith("Claude wants to export 2 clips", editor.Claude.Export.RequestTitle, StringComparison.Ordinal);
        editor.Claude.Export.AlwaysAllowCommand.Execute(null);
        var first = await asked;

        Assert.Equal(McpPermission.Allow, editor.Settings.ExportPermission);
        Assert.Equal("done", first.Status);
        Assert.Equal([Path.Combine(outDir, "sample-cut.mkv")], first.Files);
        Assert.Equal("Lossless copy · MKV · merged", first.Settings);
        Assert.True(File.Exists(first.Files[0]));
        // The user sees it on Claude's card, not in the dialog.
        Assert.False(editor.Export.IsDialogOpen);
        Assert.Equal(ClaudeExportStage.Done, editor.Claude.Export.Stage);
        Assert.Equal("Exported sample-cut.mkv", editor.Claude.Export.Title);

        var second = await tools.Export(container: "mkv", folder: outDir);
        Assert.Equal([Path.Combine(outDir, "sample-cut (2).mkv")], second.Files);
        Assert.Equal("Exported sample-cut (2).mkv", editor.Claude.Export.Title);
        // Its card has Show in folder; nothing opens by itself.
        Assert.Empty(revealed);
        Assert.Equal("done", (await tools.GetExportStatus()).Status);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Claude_export_that_does_not_start_leaves_the_dialog_as_it_was()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        var export = editor.Export;
        export.Open();
        export.Container = "MOV";

        Assert.Contains("Export dialog open", export.PrepareForClaude(container: "MKV"), StringComparison.Ordinal);
        Assert.Equal("MOV", export.Container);
        Assert.True(export.IsDialogOpen);
        export.Close();

        Assert.Null(export.PrepareForClaude(container: "MKV", merge: false));
        Assert.Equal(("MKV", false), (export.Container, export.Merge));
        export.AbandonPreparedForClaude();

        Assert.Equal(("MOV", true), (export.Container, export.Merge));
        Assert.Equal(ExportStage.Closed, export.Stage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Claude_can_cancel_its_export()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        string outDir = Directory.CreateDirectory(Path.Combine(_dir, "cancelled")).FullName;
        editor.Settings.ExportPermission = McpPermission.Allow;
        var host = new EditorMcpHost(editor);

        Assert.Null(await host.StartExportAsync(new HighlightCut.Mcp.ExportRequest(Mode: "reencode", Folder: outDir), Ct));
        Assert.Equal("running", host.Export!.Status);
        Assert.Contains("already running", await host.StartExportAsync(new HighlightCut.Mcp.ExportRequest(), Ct), StringComparison.Ordinal);
        Assert.True(host.CancelExport());

        Assert.Equal("cancelled", host.Export!.Status);
        Assert.Equal(ClaudeExportStage.Cancelled, editor.Claude.Export.Stage);
        Assert.False(editor.Export.IsDialogOpen);
        await PumpUntil(() => editor.StatusMessage == "Export cancelled.");
        Assert.Empty(Directory.GetFiles(outDir));
        window.Close();
    }

    [AvaloniaFact]
    public async Task When_the_file_exists_the_dialog_can_ask_first()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        string outDir = Directory.CreateDirectory(Path.Combine(_dir, "exists")).FullName;
        string output = Path.Combine(outDir, "sample-cut.mp4");
        await File.WriteAllTextAsync(output, "old", Ct);
        editor.Settings.IfFileExists = FileExistsAction.Ask;
        editor.Settings.AfterExport = AfterExportAction.Nothing;
        var export = editor.Export;
        export.Open();
        export.OutputFolder = outDir;

        await export.StartAsync();

        // Nothing is written until the user answers.
        Assert.True(export.IsConfiguring);
        Assert.True(export.IsAskingAboutExisting);
        Assert.False(export.ShowExportButton);
        Assert.Equal("sample-cut.mp4 already exists.", export.ExistingFilesText);
        Assert.Equal("old", await File.ReadAllTextAsync(output, Ct));
        // Another folder is another question.
        export.OutputFolder = _dir;
        Assert.False(export.IsAskingAboutExisting);
        export.OutputFolder = outDir;
        await export.StartAsync();

        await export.OverwriteCommand.ExecuteAsync(null);
        Assert.True(export.IsDone, export.ErrorText);
        Assert.Equal([output], Directory.GetFiles(outDir));
        Assert.NotEqual("old", await File.ReadAllTextAsync(output, Ct));

        export.Open();
        await export.StartAsync();
        await export.AddNumberCommand.ExecuteAsync(null);
        Assert.True(export.IsDone, export.ErrorText);
        Assert.Equal([Path.Combine(outDir, "sample-cut (2).mp4")], export.OutputFiles);

        // Overwrite in Settings replaces without asking. (Changed settings start the dialog from them, folder too.)
        editor.Settings.IfFileExists = FileExistsAction.Overwrite;
        export.Open();
        export.OutputFolder = outDir;
        await export.StartAsync();
        Assert.True(export.IsDone, export.ErrorText);
        Assert.Equal([output], export.OutputFiles);
        Assert.Equal(2, Directory.GetFiles(outDir).Length);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reencoding_into_separate_files_shows_one_row_per_file()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        string outDir = Path.Combine(_dir, "separate");

        var export = editor.Export;
        export.Open();
        export.Mode = ExportMode.Encode;
        export.Merge = false;
        export.Container = "MKV";
        export.Video = HighlightCut.Media.Export.VideoEncoding.H264Fast;
        export.OutputFolder = outDir;
        await export.StartAsync();

        Assert.True(export.IsDone, export.ErrorText);
        Assert.Equal(["sample-cut-01.mkv", "sample-cut-02.mkv"], export.Rows.Select(r => r.Name));
        var first = await MediaProbe.ProbeAsync(Path.Combine(outDir, "sample-cut-01.mkv"), Ct);
        Assert.Equal(1.7, first.Duration, 0.1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_failed_export_says_why()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        string blocker = Path.Combine(_dir, "not-a-folder");
        await File.WriteAllTextAsync(blocker, "", Ct);

        var export = editor.Export;
        export.Open();
        export.OutputFolder = Path.Combine(blocker, "out");
        await export.StartAsync();

        Assert.True(export.HasError);
        Assert.Equal("Export failed", export.Title);
        Assert.False(export.IsRunning);
        Assert.True(export.IsExporting);
        export.Close();
        Assert.False(export.IsDialogOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_hidden_export_that_fails_says_so_and_shows_why_when_reopened()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        string blocker = Path.Combine(_dir, "not-a-folder");
        await File.WriteAllTextAsync(blocker, "", Ct);

        var export = editor.Export;
        export.Open();
        export.OutputFolder = Path.Combine(blocker, "out");
        var running = export.StartAsync();
        export.Dismiss();
        await running;

        Assert.False(export.IsDialogOpen);
        Assert.StartsWith("Export failed: ", editor.StatusMessage, StringComparison.Ordinal);
        export.Open();
        Assert.True(export.IsDialogOpen);
        Assert.Equal("Export failed", export.Title);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cancelling_an_export_removes_its_files()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        string outDir = Directory.CreateDirectory(Path.Combine(_dir, "cancel")).FullName;

        var export = editor.Export;
        export.Open();
        export.Mode = ExportMode.Encode;
        export.OutputFolder = outDir;
        var running = export.StartAsync();
        export.Close();
        await running;

        Assert.False(export.IsDialogOpen);
        Assert.Equal("Export cancelled.", editor.StatusMessage);
        Assert.Empty(Directory.GetFiles(outDir));
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_export_started_right_after_a_cancel_is_left_alone_by_the_old_one()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);

        var export = editor.Export;
        export.Open();
        export.Mode = ExportMode.Encode;
        export.OutputFolder = Path.Combine(_dir, "first");
        var first = export.StartAsync();
        export.CancelExport();
        Assert.True(export.IsConfiguring);
        export.OutputFolder = Path.Combine(_dir, "second");
        var second = export.StartAsync();
        await first;
        await second;

        Assert.True(export.IsDone, export.ErrorText);
        Assert.Equal(ExportOutcome.Done, export.Outcome);
        Assert.Equal([Path.Combine(_dir, "second", "sample-cut.mp4")], export.OutputFiles);
        Assert.True(File.Exists(export.OutputFiles[0]));
        Assert.Equal("Exported sample-cut.mp4", editor.StatusMessage);
        window.Close();
    }
}
