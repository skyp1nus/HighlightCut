using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OurCut.Core.Editing;
using OurCut.Core.Editing.Commands;
using OurCut.Core.Model;
using OurCut.Core.Transcripts;

namespace OurCut.App.ViewModels;

/// <summary>The sidebar's tabs.</summary>
public enum SidebarTab
{
    Clips,
    Transcript,
}

public sealed partial class EditorViewModel
{
    private double? _playUntil;

    /// <summary>The Transcript tab and the timeline's transcript lane.</summary>
    public TranscriptPanelViewModel TranscriptPanel { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClipsTab), nameof(IsTranscriptTab), nameof(SidebarHint), nameof(SidebarHintTip))]
    public partial SidebarTab Tab { get; set; }

    public bool IsClipsTab => Tab == SidebarTab.Clips;
    public bool IsTranscriptTab => Tab == SidebarTab.Transcript;

    /// <summary>Right side of the sidebar header: "Drag to reorder", "Transcribing 34%", "English · parakeet v3".</summary>
    public string SidebarHint => IsClipsTab ? "Drag to reorder" : TranscriptPanel.Hint;

    public string? SidebarHintTip => IsClipsTab ? null : TranscriptPanel.HintTip;

    [RelayCommand]
    private void ShowClips() => Tab = SidebarTab.Clips;

    [RelayCommand]
    private void ShowTranscript()
    {
        Tab = SidebarTab.Transcript;
        TranscriptPanel.RevealCurrentWord();
    }

    /// <summary>
    /// The transcript lane under the video track (timeline toolbar chip "Transcript"). It is the same choice as
    /// Settings → Transcription → "Transcribe when a video is opened": on, the open video is transcribed (and every
    /// later one); off, a transcription under way stops.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranscriptLaneVisible), nameof(TimelineHeight), nameof(TimelineTracksHeight))]
    public partial bool ShowTranscriptLane { get; set; }

    public bool IsTranscriptLaneVisible => ShowTranscriptLane && HasFile;

    /// <summary>The timeline panel: 212 px, 20 more with the transcript lane.</summary>
    public double TimelineHeight => IsTranscriptLaneVisible ? 232 : 212;

    /// <summary>Ruler and tracks: 158 px, 20 more with the transcript lane.</summary>
    public double TimelineTracksHeight => IsTranscriptLaneVisible ? 178 : 158;

    [RelayCommand]
    private void ToggleTranscriptLane() => ShowTranscriptLane = !ShowTranscriptLane;

    partial void OnShowTranscriptLaneChanged(bool value)
    {
        RaiseTimelineChanged();
        if (IsDemo || _showingChips)
            return;
        Settings.TranscribeOnOpen = value;
        if (!HasFile)
            return;
        // Without a model the lane says how to get one.
        if (value)
            StartTranscription();
        else
            Media?.StopTranscription();
    }

    /// <summary>"Keep as clip": a clip for the words, placed among the clips by source position and selected. One undo step.</summary>
    /// <remarks>Words partly in a clip already get a clip for the rest; words wholly in one are refused, with a message.</remarks>
    public void KeepWords(double start, double end, string label) => TryEdit(() => Select(Find(Session.KeepRange(start, end, label).Id)));

    /// <summary>
    /// "Cut out": cuts the range out of the included clips as one undo step; with no clips the whole video is kept first,
    /// as Claude's cuts do. Returns false (and says why) when the range is not in the output.
    /// </summary>
    public bool CutWords(double start, double end, string description)
    {
        var project = Session.Project;
        bool noClips = project.Clips.IsEmpty;
        var targets = noClips ? [new TimeRange(0, project.SourceDuration)] : project.IncludedClips.Select(c => new TimeRange(c.Start, c.End)).ToList();
        if (!targets.Any(c => end > c.Start && start < c.End))
        {
            ShowMessage("Those words are not in the output.");
            return false;
        }
        IEditCommand cut = new CutRangesCommand([new TimeRange(start, end)], Description: description);
        if (noClips)
            cut = new BatchCommand(cut.Name, description, [new AddClipCommand(0, project.SourceDuration, project.Name), cut]);
        return TryEdit(() => Session.Execute(cut));
    }

    /// <summary>"Play selection": plays from <paramref name="start"/> and pauses at <paramref name="end"/>.</summary>
    public void PlayRange(double start, double end)
    {
        _playUntil = null;
        SetTime(start);
        if (!IsPlaying)
            TogglePlay();
        _playUntil = Math.Min(end, Duration);
    }

    private void CheckPlayUntil()
    {
        // A seek of the user's ends "Play selection"; playback goes on from there.
        if (!_timeFromPlayer)
            _playUntil = null;
        if (_playUntil is not { } until || Time < until)
            return;
        StopPlayback();
        // Not from inside the Time change that got here: the player would be left at the overshoot.
        Dispatcher.UIThread.Post(() => SetTime(until));
    }

    /// <summary>What is still being analysed, without transcription (the status bar shows that as its own item).</summary>
    private string? AnalysisActivity
    {
        get
        {
            if (Media?.Activity is not { } activity)
                return null;
            var parts = activity.Split(" · ").Where(p => !p.StartsWith("transcribing ", StringComparison.Ordinal)).ToList();
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    private TranscriptPanelViewModel CreateTranscriptPanel()
    {
        var panel = new TranscriptPanelViewModel(this) { FillerWords = FillerWords.All(Settings.FillerWords) };
        Settings.FillerWordsChanged += (_, _) => panel.FillerWords = FillerWords.All(Settings.FillerWords);
        panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranscriptPanelViewModel.Hint))
                OnPropertyChanged(nameof(SidebarHint));
            else if (e.PropertyName == nameof(TranscriptPanelViewModel.HintTip))
                OnPropertyChanged(nameof(SidebarHintTip));
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HasFile))
            {
                OnPropertyChanged(nameof(IsTranscriptLaneVisible));
                OnPropertyChanged(nameof(TimelineHeight));
                OnPropertyChanged(nameof(TimelineTracksHeight));
            }
        };
        return panel;
    }
}
