using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using OurCut.Core.Model;
using OurCut.Core.Transcripts;
using OurCut.Media.Analysis;
using OurCut.Media.Caching;
using OurCut.Media.Previews;
using OurCut.Media.Probing;
using OurCut.Media.Tools;
using OurCut.Transcription;

namespace OurCut.App.Services;

/// <summary>
/// Preview of a real media file. Keyframes and the waveform are read from the cache or extracted with ffmpeg/ffprobe
/// in the background, both at once, and appear on the timeline as they arrive; the editor is usable immediately.
/// Silences come from the waveform. Thumbnails are made alongside only when the timeline shows them
/// (<see cref="ExtractThumbnails"/>, the Frames chip). Scene detection and transcription go through the whole video, so
/// they run only when asked for (<see cref="DetectScenes"/>, <see cref="StartTranscription"/>); what an earlier run
/// cached is shown straight away. How long each part took is kept for Copy diagnostics.
/// </summary>
public sealed class MediaPreview : IMediaPreview, IDisposable
{
    /// <summary>How much excluded thumbnails are desaturated, like CSS <c>grayscale(0.8)</c>.</summary>
    private const double ExcludedGrey = 0.8;

    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#1C1C1C"));

    /// <summary>The parts <see cref="AnalysisTimes"/> lists, in this order.</summary>
    private static readonly string[] TimedParts = ["keyframes", "thumbnails", "waveform", "scenes", "transcript"];

    private readonly CancellationTokenSource _cts = new();
    private readonly MediaCache? _cache;
    private readonly List<Thumbnail> _thumbnails = [];
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource<IReadOnlyList<double>> _keyframesReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, string> _times = new();

    private readonly int _expectedThumbnails;
    private double[] _keyframes = [];
    private double _keyframeProgress;
    private int _thumbnailsRequested;
    private CancellationTokenSource? _thumbnailsCts;
    private Task _thumbnailsTask = Task.CompletedTask;
    private volatile bool _makingThumbnails;
    private volatile bool _thumbnailsComplete;
    private volatile bool _analysing;
    private volatile bool _detectingScenes;
    private int _scenesRequested;
    private CancellationTokenSource? _scenesCts;
    private volatile string? _error;
    private SceneScores? _scenes;
    private readonly TaskCompletionSource _mainAnalysisDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _transcribeCts;
    private string? _transcriptKey;
    private Transcript? _transcript;
    private volatile TranscriptState _transcriptState;
    private double _transcriptProgress;
    private volatile string? _transcriptError;
    private (int Filled, bool Complete, SilenceAnalysis Result)? _silences;
    private (int Filled, SceneAnalysis Result)? _sceneChanges;
    private int _changePending;
    private bool _disposed;

    private sealed record Thumbnail(double Time, Bitmap Color, Bitmap Grey);

    public MediaPreview(MediaInfo info, MediaCache? cache)
    {
        Info = info;
        _cache = cache;
        Waveform = WaveformExtractor.Create(info);
        _expectedThumbnails = info.Video is null ? 0 : (int)Math.Floor(info.Duration / ThumbnailExtractor.IntervalFor(info.Duration)) + 1;
        if (info.Video is null)
            _keyframeProgress = 1;
    }

    public MediaInfo Info { get; }
    public WaveformData Waveform { get; }
    public double Duration => Info.Duration;
    public double FrameRate => Info.Video?.FrameRate ?? 0;
    public IReadOnlyList<double> Keyframes => Volatile.Read(ref _keyframes);
    public int AudioStreamCount => Info.Audio.Length;

    public double AspectRatio => Info.Video?.DisplaySize is var (w, h) && w > 0 && h > 0 ? (double)w / h : 16.0 / 9.0;
    public bool IsPlaceholder => false;

    /// <summary>Completes with the keyframes once they are scanned (empty if the scan failed).</summary>
    public Task<IReadOnlyList<double>> KeyframesTask => _keyframesReady.Task;

    /// <summary>
    /// Completes when keyframes, waveform, cached scenes and thumbnails asked for meanwhile are read, failed or cancelled.
    /// Scene detection (<see cref="ScenesTask"/>), transcription and thumbnails asked for later run on their own.
    /// </summary>
    public Task Analysis { get; private set; } = Task.CompletedTask;

    /// <summary>Why part of the analysis failed, if it did.</summary>
    public string? AnalysisError => _error;

    public int ThumbnailCount
    {
        get
        {
            lock (_lock)
                return _thumbnails.Count;
        }
    }

    /// <summary>Analysis progress 0..1 (keyframes, waveform and thumbnails, if asked for, weigh the same).</summary>
    public double Progress => Parts() is { Count: > 0 } parts ? parts.Average(p => p.Done) : 1;

    public bool IsAnalysing => _analysing;

    public double AnalysisProgress => Progress;

    public string? AnalysisStage => !_analysing ? null
        : Parts().Where(p => p.Done < 1).OrderBy(p => p.Done).Select(p => p.Name).FirstOrDefault() ?? "Finishing";

    /// <summary>Each part of the analysis with how far it is, 0..1.</summary>
    private List<(string Name, double Done)> Parts()
    {
        var parts = new List<(string, double)>(3);
        if (Info.Video is not null)
        {
            parts.Add(("Finding keyframes", Volatile.Read(ref _keyframeProgress)));
            if (ThumbnailsRequested)
                parts.Add(("Making thumbnails", ThumbnailProgress));
        }
        if (Info.Audio.Length > 0)
            parts.Add(("Reading the audio", Waveform.IsComplete ? 1 : (double)Waveform.Decoded / Math.Max(1, Waveform.Capacity)));
        return parts;
    }

    /// <summary>Thumbnails made so far, 0..1; 1 when none are being made.</summary>
    private double ThumbnailProgress =>
        _makingThumbnails ? Math.Min(1, (double)ThumbnailCount / Math.Max(1, _expectedThumbnails)) : 1;

    public string? AnalysisTimes =>
        _times.IsEmpty ? null : string.Join(" · ", TimedParts.Where(_times.ContainsKey).Select(part => $"{part} {_times[part]}"));

    private void Took(string part, Stopwatch watch) =>
        _times[part] = watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private void Cached(string part) => _times[part] = "cached";

    public string? Activity
    {
        get
        {
            if (_analysing)
                return $"analysing {Math.Floor(Progress * 100):0}%";
            var parts = new List<string>(3);
            if (_makingThumbnails)
                parts.Add($"making thumbnails {Math.Floor(ThumbnailProgress * 100):0}%");
            if (_transcriptState == TranscriptState.Running)
                parts.Add($"transcribing {Math.Floor(TranscriptProgress * 100):0}%");
            if (_detectingScenes)
                parts.Add($"detecting scenes {Math.Floor((Volatile.Read(ref _scenes)?.Progress ?? 0) * 100):0}%");
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    // ---- Transcription -------------------------------------------------------------------

    public Transcript? Transcript => Volatile.Read(ref _transcript);
    public TranscriptState TranscriptState => _transcriptState;
    public double TranscriptProgress => Volatile.Read(ref _transcriptProgress);
    public string? TranscriptError => _transcriptError;

    /// <summary>
    /// Transcribes the file with <paramref name="setup"/>'s model, or reads that transcript from the cache. It starts
    /// once keyframes, waveform and thumbnails asked for with the file are done (scene detection may still run) and
    /// fills in piece by piece. Asking again with the same model and language changes nothing; another model or
    /// language starts over.
    /// </summary>
    public void StartTranscription(TranscriptionSetup setup) => Transcribe(setup, cachedOnly: false);

    /// <summary>Stops a transcription under way (the Transcript chip turned off); a finished transcript stays.</summary>
    public void StopTranscription()
    {
        if (_transcriptState is not (TranscriptState.Waiting or TranscriptState.Running))
            return;
        _transcribeCts?.Cancel();
        _transcribeCts = null;
        _transcriptKey = null;
        _transcriptState = TranscriptState.None;
        Volatile.Write(ref _transcript, null);
        Volatile.Write(ref _transcriptProgress, 0);
        NotifyChanged();
    }

    /// <summary>Shows the transcript <paramref name="setup"/>'s model made earlier, if the cache has it; transcribes nothing.</summary>
    public void LoadTranscript(TranscriptionSetup setup)
    {
        if (_transcriptState == TranscriptState.None)
            Transcribe(setup, cachedOnly: true);
    }

    private void Transcribe(TranscriptionSetup setup, bool cachedOnly)
    {
        if (_disposed || (setup.Key == _transcriptKey && _transcriptState is not (TranscriptState.None or TranscriptState.Failed)))
            return;
        _transcribeCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _transcribeCts = cts;
        _transcriptKey = setup.Key;
        Volatile.Write(ref _transcript, null);
        Volatile.Write(ref _transcriptProgress, 0);
        _transcriptError = null;
        _transcriptState = cachedOnly ? TranscriptState.None : TranscriptState.Waiting;
        NotifyChanged();
        _ = Task.Run(() => TranscribeAsync(setup, cts, cachedOnly), CancellationToken.None);
    }

    private async Task TranscribeAsync(TranscriptionSetup setup, CancellationTokenSource cts, bool cachedOnly)
    {
        var ct = cts.Token;
        string language = setup.Language ?? "auto";
        string cacheName = Transcript.CacheName(setup.Model.Id, setup.Language);
        bool Current() => ReferenceEquals(_transcribeCts, cts);
        try
        {
            if (_cache?.LoadText(Info.Path, cacheName) is { } json && Transcript.FromJson(json) is { } cached)
            {
                Cached("transcript");
                Finish(cached);
                return;
            }
            if (cachedOnly)
                return;
            if (Info.Audio.Length == 0)
            {
                Finish(new Transcript(setup.Model.Id, language, []));
                return;
            }
            await _mainAnalysisDone.Task.WaitAsync(ct).ConfigureAwait(false);
            _transcriptState = TranscriptState.Running;
            NotifyChanged();
            var watch = Stopwatch.StartNew();
            using var recognizer = setup.Create();
            var words = new List<Word>();
            await ToolProcess.RunAsync("ffmpeg", SpeechAudio.Arguments(Info), (stdout, token) =>
                TranscriptionPipeline.RunAsync(stdout, Info.Duration, recognizer, (found, progress) =>
                {
                    words.AddRange(found);
                    if (!Current())
                        return;
                    Volatile.Write(ref _transcript, new Transcript(setup.Model.Id, language, [.. words], recognizer.HasApproximateTimes));
                    Volatile.Write(ref _transcriptProgress, progress);
                    NotifyChanged();
                }, token), ct).ConfigureAwait(false);
            var transcript = new Transcript(setup.Model.Id, language, [.. words], recognizer.HasApproximateTimes);
            _cache?.SaveText(Info.Path, cacheName, transcript.ToJson());
            if (Current())
                Took("transcript", watch);
            Finish(transcript);
        }
        catch (OperationCanceledException)
        {
            if (Current())
                _transcriptState = TranscriptState.None;
        }
        catch (Exception e) when (e is MediaToolException or IOException or UnauthorizedAccessException or InvalidOperationException
                                      or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                                      or System.Runtime.InteropServices.ExternalException)
        {
            if (Current())
            {
                _transcriptError = e.Message;
                _transcriptState = TranscriptState.Failed;
                NotifyChanged();
            }
        }

        void Finish(Transcript transcript)
        {
            if (!Current())
                return;
            Volatile.Write(ref _transcript, transcript);
            Volatile.Write(ref _transcriptProgress, 1);
            _transcriptState = TranscriptState.Done;
            NotifyChanged();
        }
    }

    /// <summary>Silences as the timeline shows them: every track quiet for a second or more, at the automatic level.</summary>
    public IReadOnlyList<TimeRange> Silences => SilenceAnalysis.Ranges;

    /// <summary>Scene changes at the default sensitivity, as far as the video has been scanned.</summary>
    public IReadOnlyList<double> SceneChanges => SceneAnalysis.Changes;

    public bool SilencesComplete => Info.Audio.Length == 0 || Waveform.IsComplete || Analysis.IsCompleted;

    public bool ScenesComplete => Info.Video is null || Volatile.Read(ref _scenes) is { IsComplete: true } || ScenesTask.IsCompleted;

    /// <summary>Scene detection was asked for, or its results came from the cache.</summary>
    public bool ScenesRequested => Info.Video is null || Volatile.Read(ref _scenesRequested) == 1;

    /// <summary>Completes when scene detection has finished, failed or been cancelled; done while nobody asked for it.</summary>
    public Task ScenesTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Starts scene detection (once): it reads every frame, so it waits for the rest of the analysis, and
    /// its results fill in as it goes.
    /// </summary>
    public void DetectScenes()
    {
        if (Info.Video is null || _disposed || Interlocked.Exchange(ref _scenesRequested, 1) == 1)
            return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _scenesCts = cts;
        var ct = cts.Token;
        var stopped = ScenesTask;
        ScenesTask = Task.Run(async () =>
        {
            try
            {
                // A search stopped a moment ago lets go of its ffmpeg first.
                await stopped.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                await _mainAnalysisDone.Task.WaitAsync(ct).ConfigureAwait(false);
                await Guard(() => DetectScenesAsync(ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                // A stopped search leaves nothing half done (it may have set out its scores just after the stop); one
                // asked for again since waits for this to end before it starts.
                if (ct.IsCancellationRequested && Volatile.Read(ref _scenes) is { IsComplete: false })
                    Volatile.Write(ref _scenes, null);
                if (_scenesCts is null || ReferenceEquals(_scenesCts, cts))
                    _detectingScenes = false;
                NotifyChanged();
            }
        }, CancellationToken.None);
        NotifyChanged();
    }

    /// <summary>
    /// Stops a scene detection under way (the Scenes chip turned off): what it found so far is dropped and nothing is
    /// cached. Finished or cached scene changes stay.
    /// </summary>
    public void StopScenes()
    {
        if (Info.Video is null || Volatile.Read(ref _scenes) is { IsComplete: true } || Interlocked.Exchange(ref _scenesRequested, 0) == 0)
            return;
        Interlocked.Exchange(ref _scenesCts, null)?.Cancel();
        Volatile.Write(ref _scenes, null);
        _detectingScenes = false;
        NotifyChanged();
    }

    /// <summary>Completes when the thumbnails asked for are made, failed or were stopped; done while nobody asked.</summary>
    public Task ThumbnailsTask => Volatile.Read(ref _thumbnailsTask);

    /// <summary>Thumbnails were asked for (the Frames chip is on); until then none are made.</summary>
    public bool ThumbnailsRequested => Info.Video is null || Volatile.Read(ref _thumbnailsRequested) == 1;

    /// <summary>
    /// Makes the timeline's thumbnails (once), or reads them from the cache; they appear as they arrive. Asked for while
    /// the file is being opened, they are part of <see cref="Analysis"/> (the processing screen waits for them).
    /// </summary>
    public void ExtractThumbnails()
    {
        if (Info.Video is null || _disposed || Interlocked.Exchange(ref _thumbnailsRequested, 1) == 1 || _thumbnailsComplete)
            return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _thumbnailsCts = cts;
        var ct = cts.Token;
        var stopped = _thumbnailsTask;
        _makingThumbnails = true;
        Volatile.Write(ref _thumbnailsTask, Task.Run(async () =>
        {
            try
            {
                // A run stopped a moment ago lets go of its ffmpeg (and its last thumbnail) first.
                await stopped.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                await Guard(() => ExtractThumbnailsAsync(ct)).ConfigureAwait(false);
            }
            finally
            {
                if (ReferenceEquals(_thumbnailsCts, cts))
                    _makingThumbnails = false;
                NotifyChanged();
            }
        }, CancellationToken.None));
        NotifyChanged();
    }

    /// <summary>
    /// Stops making thumbnails (the Frames chip turned off): what was made so far is dropped and nothing is cached. A
    /// finished set stays, so turning the chip back on shows it at once.
    /// </summary>
    public void StopThumbnails()
    {
        if (Info.Video is null || Interlocked.Exchange(ref _thumbnailsRequested, 0) == 0)
            return;
        Interlocked.Exchange(ref _thumbnailsCts, null)?.Cancel();
        _makingThumbnails = false;
        lock (_lock)
        {
            if (!_thumbnailsComplete)
                DropThumbnails();
        }
        NotifyChanged();
    }

    /// <summary>The timeline's silences, recomputed only when more of the waveform has been decoded.</summary>
    private SilenceAnalysis SilenceAnalysis
    {
        get
        {
            if (Info.Audio.Length == 0)
                return OurCut.Media.Analysis.SilenceAnalysis.None;
            int filled = Waveform.Filled;
            bool complete = Waveform.IsComplete;
            if (_silences is { } cached && cached.Filled == filled && cached.Complete == complete)
                return cached.Result;
            var result = SilenceDetector.Find(Waveform);
            _silences = (filled, complete, result);
            return result;
        }
    }

    private SceneAnalysis SceneAnalysis
    {
        get
        {
            if (Volatile.Read(ref _scenes) is not { } scores)
                return new SceneAnalysis([], SceneDetector.DefaultThreshold, Info.Video is null, 0);
            int filled = scores.Filled;
            if (_sceneChanges is { } cached && cached.Filled == filled && cached.Result.IsComplete == scores.IsComplete)
                return cached.Result;
            var result = scores.Analyse();
            _sceneChanges = (filled, result);
            return result;
        }
    }

    /// <summary>Silences with other settings (for Claude); null when the file has no audio.</summary>
    public SilenceAnalysis? FindSilences(double minDuration, double? thresholdDb, IReadOnlyList<int>? streams) =>
        Info.Audio.Length == 0 ? null : SilenceDetector.Find(Waveform, minDuration, thresholdDb, streams);

    /// <summary>Scene changes at another sensitivity (for Claude); null when the file has no video.</summary>
    public SceneAnalysis? FindSceneChanges(double threshold) =>
        Info.Video is null ? null
        : Volatile.Read(ref _scenes) is { } scores ? scores.Analyse(threshold)
        : new SceneAnalysis([], threshold, false, 0);

    public event EventHandler? Changed;

    /// <summary>Starts the background analysis.</summary>
    public void Start()
    {
        if (_analysing || _disposed)
            return;
        _analysing = true;
        var ct = _cts.Token;
        Analysis = Task.Run(() => AnalyseAsync(ct), CancellationToken.None);
    }

    private async Task AnalyseAsync(CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(
                Guard(() => ScanKeyframesAsync(ct)),
                Guard(() => ExtractWaveformAsync(ct)),
                Guard(() => Task.Run(LoadCachedScenes, ct))).ConfigureAwait(false);
            // Thumbnails asked for meanwhile (the Frames chip on when the file opens) are part of opening it; stopped, they
            // end at once. The editor asks on the UI thread as it loads the file, so this looks once that is through.
            await Guard(async () =>
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, ct).GetTask().ConfigureAwait(false);
                for (Task thumbnails; !(thumbnails = Volatile.Read(ref _thumbnailsTask)).IsCompleted;)
                    await thumbnails.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }).ConfigureAwait(false);
        }
        finally
        {
            _keyframesReady.TrySetResult(Keyframes);
            _analysing = false;
            _mainAnalysisDone.TrySetResult();
            NotifyChanged();
        }
    }

    /// <summary>Scene changes found when the file was open before: shown, though nobody asked this time.</summary>
    private void LoadCachedScenes()
    {
        if (Info.Video is null || _cache?.LoadSceneScores(Info.Path) is not { } cached)
            return;
        Volatile.Write(ref _scenes, cached);
        Cached("scenes");
        Volatile.Write(ref _scenesRequested, 1);
    }

    private async Task DetectScenesAsync(CancellationToken ct)
    {
        if (Info.Video is null || ct.IsCancellationRequested || Volatile.Read(ref _scenes) is { IsComplete: true })
            return;
        if (_cache?.LoadSceneScores(Info.Path) is { } cached)
        {
            Volatile.Write(ref _scenes, cached);
            Cached("scenes");
            return;
        }
        var scores = SceneDetector.Create(Info);
        Volatile.Write(ref _scenes, scores);
        _detectingScenes = true;
        NotifyChanged();
        var watch = Stopwatch.StartNew();
        await SceneDetector.DetectAsync(Info, scores, NotifyChanged, ct).ConfigureAwait(false);
        Took("scenes", watch);
        _cache?.SaveSceneScores(Info.Path, scores);
    }

    /// <summary>One failing part (say a broken audio stream) does not stop the others.</summary>
    private async Task Guard(Func<Task> part)
    {
        try
        {
            await part().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is MediaToolException or IOException or UnauthorizedAccessException)
        {
            _error ??= e.Message;
        }
    }

    private async Task ScanKeyframesAsync(CancellationToken ct)
    {
        if (Info.Video is null)
            return;
        double[]? keyframes = _cache?.LoadKeyframes(Info.Path);
        if (keyframes is not null)
        {
            Cached("keyframes");
        }
        else
        {
            var watch = Stopwatch.StartNew();
            keyframes = await KeyframeScanner.ScanAsync(Info, new Reporter<double>(f =>
            {
                Volatile.Write(ref _keyframeProgress, f);
                NotifyChanged();
            }), ct).ConfigureAwait(false);
            Took("keyframes", watch);
            _cache?.SaveKeyframes(Info.Path, keyframes);
        }
        Volatile.Write(ref _keyframes, keyframes);
        Volatile.Write(ref _keyframeProgress, 1);
        _keyframesReady.TrySetResult(keyframes);
        NotifyChanged();
    }

    private async Task ExtractWaveformAsync(CancellationToken ct)
    {
        if (Info.Audio.Length == 0)
            return;
        if (_cache?.LoadWaveform(Info.Path) is { } cached && cached.StreamCount == Info.Audio.Length)
        {
            Waveform.CopyFrom(cached);
            Cached("waveform");
            NotifyChanged();
            return;
        }
        var watch = Stopwatch.StartNew();
        await WaveformExtractor.ExtractAsync(Info, Waveform, NotifyChanged, ct).ConfigureAwait(false);
        Took("waveform", watch);
        _cache?.SaveWaveform(Info.Path, Waveform);
    }

    private async Task ExtractThumbnailsAsync(CancellationToken ct)
    {
        if (Info.Video is null)
            return;
        int height = ThumbnailExtractor.DefaultHeight;
        if (_cache?.LoadThumbnails(Info.Path, height) is { Count: > 0 } cached)
        {
            foreach (var frame in cached)
                Add(frame, ct);
            Cached("thumbnails");
        }
        else
        {
            var frames = new List<ThumbnailFrame>();
            var watch = Stopwatch.StartNew();
            await ThumbnailExtractor.ExtractAsync(Info, frame =>
            {
                frames.Add(frame);
                Add(frame, ct);
            }, height, cancellationToken: ct).ConfigureAwait(false);
            Took("thumbnails", watch);
            _cache?.SaveThumbnails(Info.Path, frames);
        }
        lock (_lock)
        {
            // Stopped just now, the set was dropped (or is being): it is not complete.
            if (!ct.IsCancellationRequested)
                _thumbnailsComplete = true;
        }
    }

    /// <summary>Adds a thumbnail unless the preview is closed or the run it came from was stopped.</summary>
    private void Add(ThumbnailFrame frame, CancellationToken ct)
    {
        var color = ToBitmap(frame.Bgra, frame.Width, frame.Height);
        var grey = ToBitmap(Desaturate(frame.Bgra, ExcludedGrey), frame.Width, frame.Height);
        lock (_lock)
        {
            if (_disposed || ct.IsCancellationRequested)
            {
                color.Dispose();
                grey.Dispose();
                return;
            }
            _thumbnails.Add(new Thumbnail(frame.Time, color, grey));
        }
        NotifyChanged();
    }

    private static Bitmap ToBitmap(byte[] bgra, int width, int height)
    {
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(),
                new PixelSize(width, height), new Vector(96, 96), width * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Moves each pixel towards its luminance (Rec. 709 weights, as CSS grayscale()).</summary>
    internal static byte[] Desaturate(byte[] bgra, double amount)
    {
        var result = new byte[bgra.Length];
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            double b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            result[i] = (byte)Math.Round(b + (y - b) * amount);
            result[i + 1] = (byte)Math.Round(g + (y - g) * amount);
            result[i + 2] = (byte)Math.Round(r + (y - r) * amount);
            result[i + 3] = 255;
        }
        return result;
    }

    /// <summary>
    /// Raises <see cref="Changed"/> on the UI thread, at most about ten times a second however fast
    /// thumbnails arrive.
    /// </summary>
    private void NotifyChanged()
    {
        if (Interlocked.Exchange(ref _changePending, 1) == 1)
            return;
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await Task.Delay(100).ConfigureAwait(true);
            Volatile.Write(ref _changePending, 0);
            if (!_disposed)
                Changed?.Invoke(this, EventArgs.Empty);
        }, DispatcherPriority.Background);
    }

    // ---- Drawing -------------------------------------------------------------------------

    public double AudioPeak(int stream, double startTime, double endTime) => Waveform.Peak(stream, startTime, endTime);

    public void DrawFrame(DrawingContext context, Rect rect, double time, FrameLook look, int variant)
    {
        Bitmap? bitmap;
        lock (_lock)
        {
            var thumb = Nearest(time);
            bitmap = thumb is null ? null : look == FrameLook.Excluded ? thumb.Grey : thumb.Color;
            if (bitmap is null)
            {
                context.FillRectangle(look == FrameLook.Player ? Brushes.Black : PendingBrush, rect);
                return;
            }
            var size = new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            if (look == FrameLook.Player)
            {
                // Until playback arrives the player shows the nearest thumbnail, letterboxed.
                context.FillRectangle(Brushes.Black, rect);
                context.DrawImage(bitmap, new Rect(size), Fit(size, rect));
            }
            else
            {
                context.DrawImage(bitmap, Cover(size, rect.Size), rect);
            }
        }
    }

    /// <summary>Frees every thumbnail. Call with the lock held.</summary>
    private void DropThumbnails()
    {
        foreach (var t in _thumbnails)
        {
            t.Color.Dispose();
            t.Grey.Dispose();
        }
        _thumbnails.Clear();
    }

    /// <summary>The thumbnail closest in time. Call with the lock held.</summary>
    private Thumbnail? Nearest(double time)
    {
        if (_thumbnails.Count == 0)
            return null;
        int lo = 0, hi = _thumbnails.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_thumbnails[mid].Time < time)
                lo = mid + 1;
            else
                hi = mid;
        }
        if (lo > 0 && time - _thumbnails[lo - 1].Time < _thumbnails[lo].Time - time)
            lo--;
        return _thumbnails[lo];
    }

    /// <summary>The part of an image that fills <paramref name="target"/> without distortion (centre crop).</summary>
    internal static Rect Cover(Size image, Size target)
    {
        if (target.Width <= 0 || target.Height <= 0)
            return new Rect(image);
        double scale = Math.Max(target.Width / image.Width, target.Height / image.Height);
        double w = target.Width / scale, h = target.Height / scale;
        return new Rect((image.Width - w) / 2, (image.Height - h) / 2, w, h);
    }

    /// <summary>Where an image goes to fit entirely inside <paramref name="target"/>, centred.</summary>
    internal static Rect Fit(Size image, Rect target)
    {
        double scale = Math.Min(target.Width / image.Width, target.Height / image.Height);
        double w = image.Width * scale, h = image.Height * scale;
        return new Rect(target.X + (target.Width - w) / 2, target.Y + (target.Height - h) / 2, w, h);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            DropThumbnails();
        }
        _cts.Cancel();
        // The analysis still holds the token until it notices the cancellation.
        Task.WhenAll(Analysis, ScenesTask, Volatile.Read(ref _thumbnailsTask)).ContinueWith(_ => _cts.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>An <see cref="IProgress{T}"/> that reports on the calling thread (no UI marshalling).</summary>
    private sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
