using System.Globalization;
using Avalonia;
using Avalonia.Media;
using HighlightCut.Core.Model;
using HighlightCut.Core.Transcripts;
using HighlightCut.Media.Analysis;
using HighlightCut.Transcription;

namespace HighlightCut.App.Services;

/// <summary>One video of the timeline with its preview: where it starts, how long it is and what its file is called.</summary>
public sealed record PreviewPart(int SourceId, string Name, double Offset, double Duration, IMediaPreview Preview)
{
    public double End => Offset + Duration;
}

/// <summary>
/// The previews of a project's videos, end to end, as one preview of the timeline. Each file is still read and cached on
/// its own (<see cref="MediaPreview"/>); this moves what they found (keyframes, silences, scene changes, the transcript)
/// by each video's place on the timeline, draws each frame from the video under it and passes every request on to all
/// of them. Transcription runs one video after another. It owns none of the previews: the editor does, and makes a new
/// one of these whenever the videos change.
/// </summary>
public sealed class TimelinePreview : IMediaPreview, IDisposable
{
    private readonly HashSet<IMediaPreview> _transcribing = [];
    private TranscriptionSetup? _transcription;
    private bool _disposed;
    private Merged<double>? _keyframes;
    private Merged<double>? _scenes;
    private Merged<TimeRange>? _silences;
    private (object?[] Parts, Transcript? Result)? _transcript;

    public TimelinePreview(IReadOnlyList<PreviewPart> parts)
    {
        if (parts.Count == 0)
            throw new ArgumentException("A timeline needs at least one video.", nameof(parts));
        Parts = parts;
        foreach (var part in parts)
            part.Preview.Changed += OnPartChanged;
    }

    /// <summary>The videos in timeline order.</summary>
    public IReadOnlyList<PreviewPart> Parts { get; }

    /// <summary>The previews of a project's videos, from <paramref name="previews"/> by video id.</summary>
    public static IReadOnlyList<PreviewPart> PartsOf(Project project, IReadOnlyDictionary<int, IMediaPreview> previews) =>
        [.. project.Sources.Where(s => previews.ContainsKey(s.Id))
            .Select(s => new PreviewPart(s.Id, s.FileName, project.OffsetOf(s.Id), s.Duration, previews[s.Id]))];

    /// <summary>The video at a time on the timeline (a join belongs to the video that starts there).</summary>
    public PreviewPart PartAt(double time)
    {
        for (int i = 0; i < Parts.Count - 1; i++)
        {
            if (time < Parts[i].End)
                return Parts[i];
        }
        return Parts[^1];
    }

    public event EventHandler? Changed;

    private void OnPartChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        if (_transcription is not null)
            TranscribeNext();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public double Duration => Parts[^1].End;
    public double FrameRate => Parts.Select(p => p.Preview.FrameRate).FirstOrDefault(f => f > 0);
    public int AudioStreamCount => Parts.Max(p => p.Preview.AudioStreamCount);
    public double AspectRatio => Parts[0].Preview.AspectRatio;
    public bool IsPlaceholder => Parts.Any(p => p.Preview.IsPlaceholder);
    public bool IsPlayable => Parts.All(p => p.Preview.IsPlayable);

    // ---- Keyframes, waveform, scenes, thumbnails -----------------------------------------------

    public IReadOnlyList<double> Keyframes => Merge(ref _keyframes, p => p.Preview.Keyframes, (t, offset) => t + offset);
    public bool KeyframesRequested => Parts.All(p => p.Preview.KeyframesRequested);
    public bool KeyframesComplete => Parts.All(p => p.Preview.KeyframesComplete);
    public void ScanKeyframes() => ForEach(p => p.ScanKeyframes());
    public void StopKeyframes() => ForEach(p => p.StopKeyframes());

    public async Task<IReadOnlyList<double>> ReadKeyframesAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(Parts.Select(p => p.Preview.ReadKeyframesAsync(cancellationToken))).ConfigureAwait(true);
        return Keyframes;
    }

    public bool WaveformRequested => Parts.All(p => p.Preview.WaveformRequested);
    public void ReadWaveform() => ForEach(p => p.ReadWaveform());
    public void StopWaveform() => ForEach(p => p.StopWaveform());

    public Task ReadWaveformAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(Parts.Select(p => p.Preview.ReadWaveformAsync(cancellationToken)));

    public IReadOnlyList<TimeRange> Silences =>
        Merge(ref _silences, p => p.Preview.Silences, (r, offset) => new TimeRange(r.Start + offset, r.End + offset));

    public IReadOnlyList<double> SceneChanges => Merge(ref _scenes, p => p.Preview.SceneChanges, (t, offset) => t + offset);
    public bool SilencesComplete => Parts.All(p => p.Preview.SilencesComplete);
    public bool ScenesComplete => Parts.All(p => p.Preview.ScenesComplete);
    public bool ScenesRequested => Parts.All(p => p.Preview.ScenesRequested);
    public void DetectScenes() => ForEach(p => p.DetectScenes());
    public void StopScenes() => ForEach(p => p.StopScenes());
    public bool ThumbnailsRequested => Parts.All(p => p.Preview.ThumbnailsRequested);
    public void ExtractThumbnails() => ForEach(p => p.ExtractThumbnails());
    public void StopThumbnails() => ForEach(p => p.StopThumbnails());

    /// <summary>
    /// Each video's silences, on the timeline. A track number a video does not have is left out for it; the level is the
    /// first video's (each video's own follows its noise floor).
    /// </summary>
    public SilenceAnalysis? FindSilences(double minDuration, double? thresholdDb, IReadOnlyList<int>? streams)
    {
        var found = Parts.Select(p => (Part: p, Result: p.Preview.FindSilences(minDuration, thresholdDb,
                streams is null || p.Preview.AudioStreamCount == 0 ? streams
                : streams.Where(s => s < p.Preview.AudioStreamCount).ToList() is { Count: > 0 } own ? own : null)))
            .Where(x => x.Result is not null).ToList();
        if (found.Count == 0)
            return null;
        return new SilenceAnalysis(
            [.. found.SelectMany(x => x.Result!.Ranges.Select(r => new TimeRange(r.Start + x.Part.Offset, r.End + x.Part.Offset)))],
            found[0].Result!.ThresholdDb, found.Min(x => x.Result!.NoiseFloorDb), found.All(x => x.Result!.IsComplete));
    }

    public SceneAnalysis? FindSceneChanges(double threshold)
    {
        var found = Parts.Select(p => (Part: p, Result: p.Preview.FindSceneChanges(threshold))).Where(x => x.Result is not null).ToList();
        if (found.Count == 0)
            return null;
        double total = found.Sum(x => x.Part.Duration);
        return new SceneAnalysis([.. found.SelectMany(x => x.Result!.Changes.Select(t => t + x.Part.Offset))], threshold,
            found.All(x => x.Result!.IsComplete), total > 0 ? found.Sum(x => x.Result!.Progress * x.Part.Duration) / total : 1);
    }

    // ---- Transcript --------------------------------------------------------------------------

    /// <summary>Every video's words, moved to its place on the timeline; null until one of them has any.</summary>
    public Transcript? Transcript
    {
        get
        {
            object?[] parts = [.. Parts.Select(p => (object?)p.Preview.Transcript)];
            if (_transcript is { } cached && cached.Parts.SequenceEqual(parts, ReferenceEqualityComparer.Instance))
                return cached.Result;
            var own = Parts.Select((p, i) => (Index: i, Part: p, p.Preview.Transcript)).Where(x => x.Transcript is not null).ToList();
            var result = own.Count == 0 ? null
                : new Transcript(own[0].Transcript!.Model, own[0].Transcript!.Language,
                    [.. own.SelectMany(x => MovedWords(x.Index, x.Transcript!.Words, x.Part.Offset))],
                    own.Any(x => x.Transcript!.ApproximateTimes));
            _transcript = (parts, result);
            return result;
        }
    }

    /// <summary>Each video's words moved to the timeline, kept so the same word stays the same object as more arrive.</summary>
    private readonly Dictionary<int, (List<Word> Own, List<Word> Moved)> _movedWords = [];

    private List<Word> MovedWords(int part, IReadOnlyList<Word> words, double offset)
    {
        if (!_movedWords.TryGetValue(part, out var cache) || cache.Own.Count > words.Count
            || Enumerable.Range(0, cache.Own.Count).Any(i => !ReferenceEquals(cache.Own[i], words[i])))
            _movedWords[part] = cache = ([], []);
        for (int i = cache.Own.Count; i < words.Count; i++)
        {
            cache.Own.Add(words[i]);
            cache.Moved.Add(words[i] with { Start = words[i].Start + offset, End = words[i].End + offset });
        }
        return cache.Moved;
    }

    public TranscriptState TranscriptState
    {
        get
        {
            var states = Parts.Select(p => p.Preview.TranscriptState).ToList();
            if (states.Contains(TranscriptState.Running))
                return TranscriptState.Running;
            if (states.Contains(TranscriptState.Waiting))
                return TranscriptState.Waiting;
            if (states.All(s => s is TranscriptState.Done or TranscriptState.Failed))
                return states.Contains(TranscriptState.Failed) ? TranscriptState.Failed : TranscriptState.Done;
            // Some videos are done and the next one is about to start.
            return _transcription is not null && states.Contains(TranscriptState.Done) ? TranscriptState.Running : TranscriptState.None;
        }
    }

    public double TranscriptProgress =>
        Parts.Sum(p => p.Duration * (p.Preview.TranscriptState == TranscriptState.Done ? 1 : p.Preview.TranscriptProgress)) / Math.Max(1e-9, Duration);

    public string? TranscriptError => Parts.Select(p => p.Preview.TranscriptError).FirstOrDefault(e => e is not null);

    /// <summary>Transcribes the videos one after another (each one uses every core), in timeline order.</summary>
    public void StartTranscription(TranscriptionSetup setup)
    {
        if (_transcription?.Key != setup.Key)
            _transcribing.Clear();
        _transcription = setup;
        TranscribeNext();
    }

    /// <summary>Starts the first video not transcribed with the setup yet, once the ones before it are through.</summary>
    private void TranscribeNext()
    {
        if (_transcription is not { } setup)
            return;
        foreach (var part in Parts)
        {
            var preview = part.Preview;
            if (_transcribing.Add(preview))
                preview.StartTranscription(setup);
            if (preview.TranscriptState is TranscriptState.Waiting or TranscriptState.Running)
                return;
        }
    }

    public void LoadTranscript(TranscriptionSetup setup) => ForEach(p => p.LoadTranscript(setup));

    public void StopTranscription()
    {
        _transcription = null;
        _transcribing.Clear();
        ForEach(p => p.StopTranscription());
    }

    // ---- Progress ----------------------------------------------------------------------------

    public bool IsAnalysing => Parts.Any(p => p.Preview.IsAnalysing);

    public double AnalysisProgress =>
        Parts.Sum(p => p.Duration * (p.Preview.IsAnalysing ? p.Preview.AnalysisProgress : 1)) / Math.Max(1e-9, Duration);

    public string? AnalysisStage => Parts.Select(p => p.Preview.AnalysisStage).FirstOrDefault(s => s is not null);

    /// <summary>What each video is still reading: "video 2: reading the audio 40%".</summary>
    public string? Activity => Join(p => p.Preview.Activity);

    public string? AnalysisTimes => Join(p => p.Preview.AnalysisTimes);

    public string? AnalysisError => Parts.Select(p => p.Preview.AnalysisError).FirstOrDefault(e => e is not null);

    private string? Join(Func<PreviewPart, string?> text)
    {
        var parts = Parts.Select((p, i) => text(p) is { } t ? $"video {(i + 1).ToString(CultureInfo.InvariantCulture)}: {t}" : null)
            .OfType<string>().ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    // ---- Drawing -----------------------------------------------------------------------------

    /// <summary>The loudest peak of an audio stream between two timeline times, over the videos that have that stream.</summary>
    public double AudioPeak(int stream, double startTime, double endTime)
    {
        double peak = 0;
        foreach (var p in Parts)
        {
            if (p.End <= startTime || p.Offset >= endTime || stream >= p.Preview.AudioStreamCount)
                continue;
            peak = Math.Max(peak, p.Preview.AudioPeak(stream, Math.Max(startTime, p.Offset) - p.Offset, Math.Min(endTime, p.End) - p.Offset));
        }
        return peak;
    }

    /// <summary>How loud a stream is over all the videos makes no single number; each video's own is measured.</summary>
    public AudioLevel? MeasureAudio(int stream) => null;

    public void DrawFrame(DrawingContext context, Rect rect, double time, FrameLook look, int variant)
    {
        var part = PartAt(time);
        part.Preview.DrawFrame(context, rect, time - part.Offset, look, variant);
    }

    private void ForEach(Action<IMediaPreview> action)
    {
        foreach (var part in Parts)
            action(part.Preview);
    }

    /// <summary>The videos' lists moved to the timeline, made again only when one of them changes.</summary>
    private IReadOnlyList<T> Merge<T>(ref Merged<T>? cache, Func<PreviewPart, IReadOnlyList<T>> list, Func<T, double, T> move)
    {
        var lists = Parts.Select(list).ToArray();
        if (cache is { } c && c.Lists.Length == lists.Length
                           && Enumerable.Range(0, lists.Length).All(i => ReferenceEquals(c.Lists[i], lists[i]) && c.Counts[i] == lists[i].Count))
            return c.Result;
        var result = new List<T>(lists.Sum(l => l.Count));
        for (int i = 0; i < lists.Length; i++)
        {
            foreach (var item in lists[i])
                result.Add(move(item, Parts[i].Offset));
        }
        cache = new Merged<T>(lists, [.. lists.Select(l => l.Count)], result);
        return result;
    }

    /// <param name="Counts">How long each list was: the waveform's silences grow in place while the audio is read.</param>
    private sealed record Merged<T>(IReadOnlyList<T>[] Lists, int[] Counts, IReadOnlyList<T> Result);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var part in Parts)
            part.Preview.Changed -= OnPartChanged;
    }
}
