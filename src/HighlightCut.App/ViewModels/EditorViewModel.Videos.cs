using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.App.Services;
using HighlightCut.Core.Editing;
using HighlightCut.Core.Editing.Commands;
using HighlightCut.Core.Model;
using HighlightCut.Media.Playback;
using HighlightCut.Media.Tools;
using HighlightCut.Transcription;

namespace HighlightCut.App.ViewModels;

/// <summary>
/// The project's videos: end to end on one timeline. Each video has its own preview (read and cached per file); with
/// several, <see cref="Media"/> is a <see cref="TimelinePreview"/> over them and the player plays them as one
/// (<see cref="MpvTimeline"/>). Adding, removing and moving videos are edits, so they undo like any other.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>
    /// Each video's preview by video id. Kept while the project is open, also for a video removed since (undo brings it
    /// back as it was); freed when another project opens.
    /// </summary>
    private readonly Dictionary<int, IMediaPreview> _previews = [];

    /// <summary>The videos in timeline order, for the Videos list (shown with two or more).</summary>
    public ObservableCollection<VideoViewModel> Videos { get; } = [];

    /// <summary>The project has more than one video: the Videos list and the clips' video names show.</summary>
    public bool HasSeveralVideos => Session.Project.Sources.Count > 1;

    /// <summary>The previews of the project's videos as the timeline shows them, with their offsets.</summary>
    public IReadOnlyList<PreviewPart> PreviewParts => Media switch
    {
        TimelinePreview timeline => timeline.Parts,
        { } media when Session.Project.Source is { } s => [new PreviewPart(s.Id, s.FileName, 0, Duration, media)],
        _ => [],
    };

    /// <summary>The preview of the video under the playhead; null without a file.</summary>
    private IMediaPreview? CurrentVideoPreview => Session.Project.SourceAt(Time) is { } s ? PreviewOf(s.Id) : null;

    /// <summary>The preview of one video, if it is open.</summary>
    public IMediaPreview? PreviewOf(int sourceId) => _previews.GetValueOrDefault(sourceId);

    /// <summary>Frees the previews that are not in <paramref name="previews"/> and keeps those.</summary>
    private void ReplacePreviews(IReadOnlyDictionary<int, IMediaPreview> previews)
    {
        var keep = previews.Values.ToHashSet();
        foreach (var old in _previews.Values.Where(p => !keep.Contains(p)).Distinct().ToList())
            (old as IDisposable)?.Dispose();
        _previews.Clear();
        foreach (var (id, preview) in previews)
            _previews[id] = preview;
    }

    /// <summary>A new video's preview, under the id it is about to get; one left there by an undone video goes.</summary>
    private void AddPreview(int sourceId, IMediaPreview preview)
    {
        if (_previews.TryGetValue(sourceId, out var old) && !ReferenceEquals(old, preview))
            (old as IDisposable)?.Dispose();
        _previews[sourceId] = preview;
    }

    /// <summary>What the timeline draws: the video's own preview with one video, a <see cref="TimelinePreview"/> with several.</summary>
    private IMediaPreview MediaFor(Project project)
    {
        var parts = TimelinePreview.PartsOf(project, _previews);
        return parts.Count == 1 && project.Sources.Count == 1 ? parts[0].Preview : new TimelinePreview(parts);
    }

    /// <summary>What the player loads: the file, or with several videos all of them end to end.</summary>
    private static string PlayerPath(Project project) =>
        MpvTimeline.PathFor([.. project.Sources.Select(s => new TimelineFile(s.Path, s.Duration, s.AudioTracks.Length))]);

    /// <summary>
    /// The videos changed (added, removed, moved; by an edit, undo or redo): the timeline, the player, the keyframes for
    /// snapping and the audio lanes follow. Only what the chips show is read for a video that came in.
    /// </summary>
    private void OnSourcesChanged(Project project)
    {
        if (Media is null)
            return;
        bool transcribing = Settings.TranscribeOnOpen || Media.TranscriptState != TranscriptState.None;
        var media = MediaFor(project);
        var keyframes = media.Keyframes;
        Session.Keyframes = keyframes;
        _appliedKeyframes = keyframes;
        Media = media;
        if (ShowKeyframes)
            media.ScanKeyframes();
        if (ShowWaveform || ShowSilences)
            media.ReadWaveform();
        if (ShowScenes)
            media.DetectScenes();
        if (ShowFrames)
            media.ExtractThumbnails();
        if (transcribing)
            StartTranscription();
        else
            LoadCachedTranscript();
        CreateAudioLanes(project);
        SyncVideos(project);
        bool wasPlaying = IsPlaying;
        StopPlayback();
        if (_player is not null && media.IsPlayable && project.Source is not null)
            _ = LoadPlayerAsync(PlayerPath(project), play: wasPlaying);
        foreach (string name in (string[])[nameof(Duration), nameof(DurationText), nameof(TransportDurationText), nameof(SourceLengthText),
                     nameof(HasSeveralVideos), nameof(PreviewParts), nameof(MediaInfoText), nameof(FrameRate), nameof(FrameText), nameof(HasSilenceData),
                     nameof(HasSceneData), nameof(CanToggleAudio), nameof(CanToggleScenes), nameof(CanToggleKeyframes), nameof(KeyframesOn),
                     nameof(WaveformOn), nameof(SilencesOn), nameof(ScenesOn), nameof(VideoAspect), nameof(HasPlayback)])
            OnPropertyChanged(name);
        RaiseTimelineChanged();
    }

    /// <summary>Updates the Videos list to match the project.</summary>
    private void SyncVideos(Project project)
    {
        Videos.Clear();
        for (int i = 0; i < project.Sources.Count; i++)
        {
            var source = project.Sources[i];
            Videos.Add(new VideoViewModel(this, source, i + 1, project.Sources.Count, project.OffsetOf(source.Id),
                project.ClipsOf(source.Id).Count()));
        }
    }

    // ---- Adding, removing, moving ---------------------------------------------------------

    /// <summary>Project menu → Add video…, the timeline's +: appends the videos picked in a file dialog.</summary>
    [RelayCommand]
    public async Task AddVideo()
    {
        if (!HasFile || Dialogs is null)
            return;
        var paths = await Dialogs.PickMediaToAddAsync().ConfigureAwait(true);
        if (paths.Count > 0)
            await AddVideosAsync(paths).ConfigureAwait(true);
    }

    /// <summary>
    /// Appends videos at the end of the timeline (a drop onto the timeline, Add video…), in the order given, as one undo
    /// step. Each is probed first; one that cannot be opened is skipped with the reason. Returns the ids they got.
    /// </summary>
    /// <param name="origin">Who adds them: Claude's <c>add_video</c> adds as <see cref="EditOrigin.Assistant"/>.</param>
    /// <param name="failed">Gets each reason a video was not added, besides the status bar.</param>
    public async Task<IReadOnlyList<int>> AddVideosAsync(IReadOnlyList<string> paths, EditOrigin origin = EditOrigin.User,
        Action<string>? failed = null)
    {
        void Fail(string message)
        {
            ShowMessage(message);
            failed?.Invoke(message);
        }

        if (!HasFile || MediaOpener is null)
        {
            failed?.Invoke(HasFile ? "Adding videos is not available." : "No video is open.");
            return [];
        }
        var opened = new List<OpenedMedia>();
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            OpeningFile = name;
            try
            {
                opened.Add(await MediaOpener.OpenAsync(path).ConfigureAwait(true));
            }
            catch (FileNotFoundException)
            {
                Fail($"{name} was not found. It may have been moved or deleted.");
            }
            catch (Exception e) when (e is MediaToolException or IOException or UnauthorizedAccessException)
            {
                Fail($"Could not open {name}: {e.Message}");
            }
            finally
            {
                OpeningFile = null;
            }
        }
        // Another project opened meanwhile: these videos were for the one before.
        if (opened.Count == 0 || !HasFile)
        {
            foreach (var o in opened)
                (o.Preview as IDisposable)?.Dispose();
            if (opened.Count > 0)
                failed?.Invoke("Another project was opened meanwhile.");
            return [];
        }
        int first = Session.Project.NextSourceId;
        for (int i = 0; i < opened.Count; i++)
            AddPreview(first + i, opened[i].Preview);
        var commands = opened.Select(o => (IEditCommand)new AddSourceCommand(o.Source)).ToList();
        try
        {
            Session.Execute(commands.Count == 1 ? commands[0] : new BatchCommand(commands[0].Name, $"Added {commands.Count} videos", commands),
                origin);
        }
        catch (EditException e)
        {
            Fail(e.Message);
            return [];
        }
        IReadOnlyList<int> ids = [.. Enumerable.Range(first, opened.Count)];
        ShowMessage(opened.Count == 1
            ? $"Added {Path.GetFileName(opened[0].Source.Path)} at {Core.Time.TimeFormat.MinutesSeconds(Session.Project.OffsetOf(first))}."
            : $"Added {opened.Count} videos at the end of the timeline.");
        return ids;
    }

    /// <summary>Claude's <c>add_video</c>: appends one video as Claude's edit. Returns why it was not added, or null.</summary>
    public async Task<string?> AddVideoForClaudeAsync(string path)
    {
        string? error = null;
        var ids = await AddVideosAsync([path], EditOrigin.Assistant, e => error ??= e).ConfigureAwait(true);
        return ids.Count > 0 ? null : error ?? $"{Path.GetFileName(path)} was not added.";
    }

    /// <summary>
    /// Removes a video and its clips, as one undo step. The Videos list asks first when the video has clips
    /// (<see cref="VideoViewModel.Remove"/>).
    /// </summary>
    public void RemoveVideo(int sourceId)
    {
        var project = Session.Project;
        if (project.FindSource(sourceId) is not { } source)
            return;
        if (project.Sources.Count == 1)
        {
            ShowMessage("The only video cannot be removed; open another one instead.");
            return;
        }
        int clips = project.ClipsOf(sourceId).Count();
        if (TryEdit(() => Session.RemoveSource(sourceId)))
            ShowMessage($"Removed {source.FileName}" + (clips == 0 ? "." : $" and its {clips} clip{(clips == 1 ? "" : "s")}.")
                        + " Undo brings it back.");
    }

    /// <summary>Moves a video to another place on the timeline (0-based); its clips go with it.</summary>
    public void MoveVideo(int sourceId, int toIndex)
    {
        var project = Session.Project;
        if (toIndex < 0 || toIndex >= project.Sources.Count || project.SourceIndexOf(sourceId) is var from && (from < 0 || from == toIndex))
            return;
        TryEdit(() => Session.MoveSource(sourceId, toIndex));
    }

    /// <summary>Moves the playhead to where a video starts.</summary>
    public void GoToVideo(int sourceId)
    {
        if (Session.Project.FindSource(sourceId) is not null)
            SetTime(Session.Project.OffsetOf(sourceId));
    }
}
