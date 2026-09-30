using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.App.Services;
using HighlightCut.Core.Time;
using HighlightCut.Core.Transcripts;
using HighlightCut.Transcription;
using HighlightCut.Transcription.Models;
using Fillers = HighlightCut.Core.Transcripts.FillerWords;

namespace HighlightCut.App.ViewModels;

public enum TranscriptPanelState
{
    NoVideo,
    NoModel,
    NotStarted,
    Waiting,
    Running,
    Done,
    Failed,
}

/// <summary>
/// The Transcript tab: the open file's transcript as paragraphs of words, search, word selection and the model download
/// when none is installed. The timeline's transcript lane draws from the same words.
/// </summary>
public sealed partial class TranscriptPanelViewModel : ViewModelBase
{
    /// <summary>The playhead is on a word until this long after it ends.</summary>
    private const double CurrentWordLinger = 0.6;

    private readonly EditorViewModel _editor;
    private readonly List<TranscriptWordViewModel> _words = [];
    private IMediaPreview? _media;
    private Transcript? _transcript;
    private IReadOnlyList<Word> _wordList = [];
    private IReadOnlyList<Phrase> _paragraphs = [];
    private TranscriptState _mediaState;
    private double _mediaProgress;
    private IReadOnlyList<WordSpan> _matches = [];
    private int _matchIndex;
    private int _selectionFirst = -1;
    private int _selectionLast = -1;
    private IReadOnlyList<string> _fillerWords = Fillers.All(Fillers.Defaults);

    public TranscriptPanelViewModel(EditorViewModel editor)
    {
        _editor = editor;
        ModelOffer = new ModelOfferViewModel(editor);
        editor.PropertyChanged += OnEditorPropertyChanged;
        editor.Session.Changed += (_, _) => UpdateOut();
        foreach (var model in editor.Settings.Models)
            model.PropertyChanged += OnModelPropertyChanged;
        editor.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.Model) or nameof(SettingsViewModel.Engine))
                Refresh();
        };
        Attach(editor.Media);
        Refresh();
    }

    // ---- State ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText), nameof(IsRunning), nameof(IsNoVideo), nameof(IsNoModel), nameof(IsNotStarted),
        nameof(IsFailed), nameof(IsStatusVisible), nameof(IsEmptyTranscript))]
    public partial TranscriptPanelState State { get; private set; }

    /// <summary>There is text to show (done, or as far as it has been transcribed).</summary>
    public bool HasText => State is TranscriptPanelState.Done or TranscriptPanelState.Running or TranscriptPanelState.Waiting;

    public bool IsRunning => State is TranscriptPanelState.Running or TranscriptPanelState.Waiting;
    public bool IsNoVideo => State == TranscriptPanelState.NoVideo;
    public bool IsNoModel => State == TranscriptPanelState.NoModel;
    public bool IsNotStarted => State == TranscriptPanelState.NotStarted;
    public bool IsFailed => State == TranscriptPanelState.Failed;

    /// <summary>Transcribed, and nothing was said.</summary>
    public bool IsEmptyTranscript => State == TranscriptPanelState.Done && _words.Count == 0;

    public string? ErrorText => _media?.TranscriptError;

    public ObservableCollection<TranscriptParagraphViewModel> Paragraphs { get; } = [];

    /// <summary>Every word; the index is the word's index in the transcript.</summary>
    public IReadOnlyList<TranscriptWordViewModel> Words => _words;

    /// <summary>Short runs of words for the timeline lane.</summary>
    public IReadOnlyList<Phrase> LaneChunks { get; private set; } = [];

    /// <summary>Changes when the words are replaced rather than added to (another transcript).</summary>
    public int LayoutVersion { get; private set; }

    /// <summary>Part of the audio transcribed, 0..1.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    /// <summary>How far into the video the words go while transcribing (the whole video otherwise).</summary>
    public double TranscribedUntil => State switch
    {
        TranscriptPanelState.Running => Progress * _editor.Duration,
        TranscriptPanelState.Waiting => 0,
        _ => _editor.Duration,
    };

    /// <summary>The line under the words while transcribing: "Transcribing · 04:57 of 14:32".</summary>
    public string TailText => State == TranscriptPanelState.Waiting
        ? "Waiting for the analysis to finish…"
        : $"Transcribing · {TimeFormat.Clock(TranscribedUntil)} of {TimeFormat.Clock(_editor.Duration)}";

    /// <summary>The sidebar header's hint on this tab: "Transcribing 34%", "English · parakeet v3" or nothing.</summary>
    public string Hint => State switch
    {
        TranscriptPanelState.Running or TranscriptPanelState.Waiting => "Transcribing " + Percent,
        TranscriptPanelState.Done => string.Join(" · ", new[] { LanguageName(_transcript?.Language), ShortModelName(ModelId) }
            .Where(s => !string.IsNullOrEmpty(s))),
        _ => "",
    };

    public string? HintTip => State == TranscriptPanelState.Done && _transcript is { ApproximateTimes: true }
        ? "Word times are estimated for this model"
        : null;

    /// <summary>The status bar item while transcribing.</summary>
    public bool IsStatusVisible => State == TranscriptPanelState.Running;

    /// <summary>"transcribing 34% · parakeet · GPU".</summary>
    public string StatusText => $"transcribing {Percent} · {EngineName} · {DeviceText}";

    private string DeviceText => _editor.IsDemo || _editor.Settings.TranscribesOnGpu ? "GPU" : "CPU";

    /// <summary>What the timeline lane says when there is no text; null when there is (or no video).</summary>
    public string? LaneMessage => State switch
    {
        TranscriptPanelState.NoModel => SuggestedModel switch
        {
            { IsDownloading: true } => "Downloading the transcription model…",
            { IsNoSpace: true } => "Not enough space for the transcription model · see the Transcript tab",
            _ => "No transcript yet · install a model in the Transcript tab",
        },
        TranscriptPanelState.NotStarted => "No transcript yet · transcribe it in the Transcript tab",
        TranscriptPanelState.Failed => "Transcription failed · see the Transcript tab",
        _ => null,
    };

    private string Percent => Math.Floor(Progress * 100).ToString(CultureInfo.InvariantCulture) + "%";

    private string? ModelId => _transcript?.Model ?? _editor.Settings.ActiveModel?.Id;

    private string EngineName => ModelId is { } id
        ? ModelCatalog.Find(id)?.Engine.ToString().ToLowerInvariant() ?? id
        : "";

    private static string ShortModelName(string? id) => id switch
    {
        null => "",
        "parakeet-tdt-0.6b-v3" => "parakeet v3",
        _ when id.StartsWith("whisper-", StringComparison.Ordinal) => "whisper " + id["whisper-".Length..],
        _ => id,
    };

    private static string? LanguageName(string? code) => code switch
    {
        "en" => "English",
        "de" => "German",
        "es" => "Spanish",
        "fr" => "French",
        "ja" => "Japanese",
        "pt" => "Portuguese",
        "uk" => "Ukrainian",
        _ => null,
    };

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EditorViewModel.Media):
                TranscribeWhenInstalled = false;
                Attach(_editor.Media);
                Query = "";
                ClearSelection();
                Refresh();
                break;
            case nameof(EditorViewModel.HasFile):
                Refresh();
                break;
            case nameof(EditorViewModel.Time):
                UpdateCurrentWord();
                break;
        }
    }

    private void Attach(IMediaPreview? media)
    {
        if (ReferenceEquals(media, _media))
            return;
        if (_media is not null)
            _media.Changed -= OnMediaChanged;
        _media = media;
        if (media is not null)
            media.Changed += OnMediaChanged;
    }

    private void OnMediaChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _media))
            Refresh();
    }

    /// <summary>Reads the transcript from the open file; cheap when nothing changed (the file's Changed fires for thumbnails too).</summary>
    private void Refresh()
    {
        var media = _editor.HasFile ? _editor.Media : null;
        var transcript = media?.Transcript;
        var mediaState = media?.TranscriptState ?? TranscriptState.None;
        double progress = media?.TranscriptProgress ?? 0;
        bool changed = mediaState != _mediaState || progress != _mediaProgress;
        _mediaState = mediaState;
        _mediaProgress = progress;
        if (!ReferenceEquals(transcript, _transcript))
        {
            _transcript = transcript;
            UpdateWords(transcript?.Words ?? []);
            changed = true;
        }
        var state = StateOf(media, mediaState);
        changed |= state != State;
        State = state;
        Progress = mediaState == TranscriptState.Done ? 1 : progress;
        if (changed)
            RaiseTexts();
    }

    private TranscriptPanelState StateOf(IMediaPreview? media, TranscriptState state) => media is null
        ? TranscriptPanelState.NoVideo
        : state switch
        {
            TranscriptState.Done => TranscriptPanelState.Done,
            TranscriptState.Running => TranscriptPanelState.Running,
            TranscriptState.Waiting => TranscriptPanelState.Waiting,
            TranscriptState.Failed => TranscriptPanelState.Failed,
            _ => _editor.IsDemo || _editor.Settings.ActiveModel is null ? TranscriptPanelState.NoModel : TranscriptPanelState.NotStarted,
        };

    private void RaiseTexts()
    {
        foreach (string name in (string[])[nameof(TranscribedUntil), nameof(TailText), nameof(Hint), nameof(HintTip), nameof(StatusText),
                     nameof(LaneMessage), nameof(ErrorText), nameof(IsEmptyTranscript), nameof(HasSelection), nameof(SelectionText)])
            OnPropertyChanged(name);
    }

    /// <summary>New words are added to the paragraphs they belong to; another transcript replaces them all.</summary>
    private void UpdateWords(IReadOnlyList<Word> words)
    {
        bool extends = _words.Count <= words.Count;
        for (int i = 0; i < _words.Count && extends; i++)
            extends = ReferenceEquals(_words[i].Word, words[i]);
        if (!extends)
        {
            _words.Clear();
            Paragraphs.Clear();
            CurrentWordIndex = -1;
            _selectionFirst = _selectionLast = -1;
            LayoutVersion++;
        }
        for (int i = _words.Count; i < words.Count; i++)
            _words.Add(new TranscriptWordViewModel(i, words[i]));
        _wordList = words;
        // With several videos, each video's words start a paragraph of their own, headed by the video's name.
        var parts = _editor.PreviewParts;
        _videoStarts = parts.Count > 1 ? [.. parts.Select((p, i) => (p.Offset, $"{i + 1} · {p.Name}"))] : [];
        _paragraphs = TranscriptLayout.Paragraphs(words, _videoStarts.Count > 1 ? [.. _videoStarts.Skip(1).Select(v => v.Offset)] : null);
        SyncParagraphs();
        LaneChunks = TranscriptLayout.Chunks(words, _paragraphs);
        MarkFillers();
        UpdateOut(redraw: false);
        UpdateMatches();
        UpdateCurrentWord();
        _editor.RaiseTimelineChanged();
    }

    private void SyncParagraphs()
    {
        for (int p = 0; p < _paragraphs.Count; p++)
        {
            var paragraph = _paragraphs[p];
            if (p < Paragraphs.Count && Paragraphs[p].FirstWord != paragraph.FirstWord)
            {
                while (Paragraphs.Count > p)
                    Paragraphs.RemoveAt(Paragraphs.Count - 1);
            }
            bool added = p == Paragraphs.Count;
            var vm = added ? new TranscriptParagraphViewModel(this, paragraph.FirstWord, paragraph.Start) { Video = VideoStartingAt(p) } : Paragraphs[p];
            var words = vm.Words;
            while (words.Count > paragraph.WordCount)
                words.RemoveAt(words.Count - 1);
            for (int i = words.Count; i < paragraph.WordCount; i++)
                words.Add(_words[paragraph.FirstWord + i]);
            if (added)
                Paragraphs.Add(vm);
        }
        while (Paragraphs.Count > _paragraphs.Count)
            Paragraphs.RemoveAt(Paragraphs.Count - 1);
    }

    /// <summary>Where each video starts and its name ("2 · part-2.mp4"), with several videos; empty with one.</summary>
    private IReadOnlyList<(double Offset, string Name)> _videoStarts = [];

    /// <summary>The name of the video paragraph <paramref name="p"/> is the first of, or null.</summary>
    private string? VideoStartingAt(int p)
    {
        if (_videoStarts.Count == 0)
            return null;
        string? Of(double t) => _videoStarts.LastOrDefault(v => t >= v.Offset - 1e-9).Name ?? _videoStarts[0].Name;
        string? own = Of(_paragraphs[p].Start);
        return p == 0 || own != Of(_paragraphs[p - 1].Start) ? own : null;
    }

    /// <summary>The paragraph a word is in, or -1.</summary>
    public int ParagraphIndexOf(int wordIndex)
    {
        int lo = 0, hi = Paragraphs.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Paragraphs[mid].FirstWord <= wordIndex)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found >= 0 && wordIndex < Paragraphs[found].FirstWord + Paragraphs[found].Words.Count ? found : -1;
    }

    // ---- Playhead -------------------------------------------------------------------------

    /// <summary>The word under the playhead, or -1.</summary>
    public int CurrentWordIndex { get; private set; } = -1;

    /// <summary>Asks the view to scroll a word into view.</summary>
    public event EventHandler<TranscriptScrollEventArgs>? ScrollRequested;

    /// <summary>Clicking a word: the playhead moves to its start.</summary>
    public void SeekToWord(int index)
    {
        if (index < 0 || index >= _words.Count)
            return;
        _editor.SetTime(_words[index].Start);
        ClearSelection();
    }

    internal void SeekTo(double time) => _editor.SetTime(time);

    /// <summary>Brings the word under the playhead to the middle of the list (opening the tab).</summary>
    public void RevealCurrentWord()
    {
        if (CurrentWordIndex >= 0)
            ScrollRequested?.Invoke(this, new TranscriptScrollEventArgs(CurrentWordIndex, center: true));
    }

    private void UpdateCurrentWord()
    {
        int index = WordAt(_editor.Time);
        if (index == CurrentWordIndex)
            return;
        if (CurrentWordIndex >= 0 && CurrentWordIndex < _words.Count)
            _words[CurrentWordIndex].IsCurrent = false;
        CurrentWordIndex = index;
        OnPropertyChanged(nameof(CurrentWordIndex));
        if (index < 0)
            return;
        _words[index].IsCurrent = true;
        if (_editor.IsPlaying)
            ScrollRequested?.Invoke(this, new TranscriptScrollEventArgs(index, center: false));
    }

    /// <summary>The last word starting at or before <paramref name="time"/>, unless the playhead has left it.</summary>
    private int WordAt(double time)
    {
        int lo = 0, hi = _words.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_words[mid].Start <= time)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found >= 0 && time <= _words[found].End + CurrentWordLinger ? found : -1;
    }

    // ---- Output ---------------------------------------------------------------------------

    /// <summary>
    /// Words whose middle is outside every included clip are shown struck through. With no clips at all nothing is
    /// marked yet, so no word is struck.
    /// </summary>
    private void UpdateOut(bool redraw = true)
    {
        var project = _editor.Session.Project;
        bool anyClips = project.Clips.Count > 0;
        var ranges = new List<(double Start, double End)>();
        // The words are on the timeline, and so are the clips' ranges.
        foreach (var clip in project.IncludedClips.Select(c => project.FindSource(c.SourceId) is null ? new HighlightCut.Core.Model.TimeRange(c.Start, c.End) : project.TimelineRange(c))
                     .OrderBy(c => c.Start))
        {
            if (ranges.Count > 0 && clip.Start <= ranges[^1].End)
                ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, clip.End));
            else
                ranges.Add((clip.Start, clip.End));
        }
        foreach (var word in _words)
        {
            double mid = (word.Start + word.End) / 2;
            int lo = 0, hi = ranges.Count - 1, found = -1;
            while (lo <= hi)
            {
                int m = (lo + hi) / 2;
                if (ranges[m].Start <= mid)
                {
                    found = m;
                    lo = m + 1;
                }
                else
                {
                    hi = m - 1;
                }
            }
            word.IsOut = anyClips && (found < 0 || mid > ranges[found].End);
        }
        if (redraw)
            _editor.RaiseTimelineChanged();
    }

    // ---- Search ---------------------------------------------------------------------------

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    /// <summary>"1 of 4", "No match", or nothing without a query.</summary>
    public string MatchText => Query.Trim().Length == 0 ? ""
        : _matches.Count == 0 ? "No match"
        : string.Create(CultureInfo.InvariantCulture, $"{CurrentMatch + 1} of {_matches.Count}");

    public bool HasMatches => _matches.Count > 0;

    private int CurrentMatch => _matches.Count == 0 ? -1 : Math.Min(_matchIndex, _matches.Count - 1);

    partial void OnQueryChanged(string value)
    {
        _matchIndex = 0;
        UpdateMatches();
    }

    [RelayCommand(CanExecute = nameof(HasMatches))]
    private void NextMatch() => GoToMatch(CurrentMatch + 1);

    [RelayCommand(CanExecute = nameof(HasMatches))]
    private void PreviousMatch() => GoToMatch(CurrentMatch - 1);

    private void GoToMatch(int index)
    {
        if (_matches.Count == 0)
            return;
        _matchIndex = (index % _matches.Count + _matches.Count) % _matches.Count;
        UpdateMatches();
        int first = _matches[_matchIndex].First;
        _editor.SetTime(_words[first].Start);
        ScrollRequested?.Invoke(this, new TranscriptScrollEventArgs(first, center: false));
    }

    private void UpdateMatches()
    {
        _matches = TranscriptSearch.Find(_wordList, Query);
        var flags = new byte[_words.Count];
        for (int m = 0; m < _matches.Count; m++)
        {
            for (int k = 0; k < _matches[m].Count; k++)
                flags[_matches[m].First + k] = m == CurrentMatch ? (byte)2 : Math.Max(flags[_matches[m].First + k], (byte)1);
        }
        for (int i = 0; i < _words.Count; i++)
        {
            _words[i].IsMatch = flags[i] > 0;
            _words[i].IsCurrentMatch = flags[i] == 2;
        }
        OnPropertyChanged(nameof(MatchText));
        OnPropertyChanged(nameof(HasMatches));
        NextMatchCommand.NotifyCanExecuteChanged();
        PreviousMatchCommand.NotifyCanExecuteChanged();
    }

    // ---- Selection ------------------------------------------------------------------------

    public bool HasSelection => _selectionFirst >= 0 && State is TranscriptPanelState.Done or TranscriptPanelState.Running or TranscriptPanelState.Waiting;

    /// <summary>"04:24.2 – 04:27.3 · 3.1 s · 3 words".</summary>
    public string SelectionText
    {
        get
        {
            if (!HasSelection)
                return "";
            var first = _words[_selectionFirst];
            var last = _words[_selectionLast];
            int n = _selectionLast - _selectionFirst + 1;
            return string.Create(CultureInfo.InvariantCulture,
                $"{Tenths(first.Start)} – {Tenths(last.End)} · {last.End - first.Start:0.0} s · {n} word{(n == 1 ? "" : "s")}");
        }
    }

    // "MM:SS.mmm" cut to tenths, as the prototype does.
    private static string Tenths(double time) => TimeFormat.MinutesSeconds(time)[..^2];

    /// <summary>Selects the words from <paramref name="anchor"/> to <paramref name="end"/>, in either order.</summary>
    public void Select(int anchor, int end)
    {
        if (_words.Count == 0)
            return;
        SetSelection(Math.Clamp(Math.Min(anchor, end), 0, _words.Count - 1), Math.Clamp(Math.Max(anchor, end), 0, _words.Count - 1));
    }

    [RelayCommand]
    private void ClearSelection() => SetSelection(-1, -1);

    private void SetSelection(int first, int last)
    {
        if (first == _selectionFirst && last == _selectionLast)
            return;
        for (int i = Math.Max(0, _selectionFirst); i <= _selectionLast && i < _words.Count; i++)
            _words[i].IsSelected = false;
        _selectionFirst = first;
        _selectionLast = last;
        for (int i = Math.Max(0, first); i <= last; i++)
            _words[i].IsSelected = true;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        _editor.RaiseTimelineChanged();
    }

    [RelayCommand]
    private void KeepSelection()
    {
        if (!HasSelection)
            return;
        var (start, end) = SelectionRange;
        _editor.KeepWords(Math.Max(0, start - 0.05), Math.Min(_editor.Duration, end + 0.1), SelectionLabel());
        ClearSelection();
    }

    [RelayCommand]
    private void CutSelection()
    {
        if (!HasSelection)
            return;
        var (start, end) = SelectionRange;
        string more = _selectionLast - _selectionFirst + 1 > 4 ? "…" : "";
        if (_editor.CutWords(Math.Max(0, start - 0.06), Math.Min(_editor.Duration, end + 0.1), $"Cut out “{SelectionLabel()}{more}”"))
            ClearSelection();
    }

    [RelayCommand]
    private void PlaySelection()
    {
        if (!HasSelection)
            return;
        var (start, end) = SelectionRange;
        _editor.PlayRange(start, end + 0.1);
    }

    private (double Start, double End) SelectionRange => (_words[_selectionFirst].Start, _words[_selectionLast].End);

    /// <summary>The first four selected words without punctuation (the new clip's name).</summary>
    private string SelectionLabel() => string.Join(' ', _words.Skip(_selectionFirst).Take(Math.Min(_selectionLast - _selectionFirst + 1, 4))
        .Select(w => new string([.. w.Text.Where(ch => ch is not ('.' or ',' or '?' or '!' or ':' or ';'))])));

    // ---- Filler words ---------------------------------------------------------------------

    /// <summary>The words marked as fillers (Settings → Transcription, every language).</summary>
    public IReadOnlyList<string> FillerWords
    {
        get => _fillerWords;
        set
        {
            if (_fillerWords.SequenceEqual(value, StringComparer.Ordinal))
                return;
            _fillerWords = value;
            MarkFillers();
            _editor.RaiseTimelineChanged();
            OnPropertyChanged();
        }
    }

    private void MarkFillers()
    {
        var flags = TranscriptSearch.MarkFillers(_wordList, _fillerWords, _paragraphs);
        for (int i = 0; i < _words.Count; i++)
            _words[i].IsFiller = flags[i];
    }

    // ---- Model download -------------------------------------------------------------------

    /// <summary>The download offered when no model is installed (the welcome tour shows the same one).</summary>
    public ModelOfferViewModel ModelOffer { get; }

    /// <summary>The model the tab offers to download (Parakeet).</summary>
    public TranscriptionModelViewModel? SuggestedModel => ModelOffer.Model;

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SuggestedModel))
            OnPropertyChanged(nameof(LaneMessage));
        if (e.PropertyName == nameof(TranscriptionModelViewModel.State))
            Refresh();
    }

    /// <summary>The tab's Download was pressed for this video: it is transcribed once the model is installed.</summary>
    internal bool TranscribeWhenInstalled { get; set; }

    [RelayCommand]
    private void DownloadModel()
    {
        if (SuggestedModel is null)
            return;
        TranscribeWhenInstalled = true;
        ModelOffer.Download();
    }

    [RelayCommand]
    private void OpenModelSettings()
    {
        _editor.Settings.Section = "Transcription";
        _editor.Settings.Open();
    }

    /// <summary>Starts transcription (not started yet, or failed).</summary>
    [RelayCommand]
    private void Transcribe()
    {
        if (_editor.StartTranscription() is { } why)
            _editor.ShowMessage(why);
    }
}

/// <summary>A word of the transcript, with how it is shown.</summary>
public sealed partial class TranscriptWordViewModel(int index, Word word) : ViewModelBase
{
    public int Index { get; } = index;
    public Word Word { get; } = word;
    public string Text => Word.Text;
    public double Start => Word.Start;
    public double End => Word.End;

    /// <summary>Under the playhead.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Part of a search match; <see cref="IsCurrentMatch"/> for the one stepped to.</summary>
    [ObservableProperty]
    public partial bool IsMatch { get; set; }

    [ObservableProperty]
    public partial bool IsCurrentMatch { get; set; }

    /// <summary>Not in the output (outside every included clip).</summary>
    [ObservableProperty]
    public partial bool IsOut { get; set; }

    [ObservableProperty]
    public partial bool IsFiller { get; set; }
}

/// <summary>A paragraph of the transcript: its time stamp and words (more arrive while transcribing).</summary>
public sealed partial class TranscriptParagraphViewModel(TranscriptPanelViewModel owner, int firstWord, double start) : ViewModelBase
{
    public int FirstWord { get; } = firstWord;
    public double Start { get; } = start;

    /// <summary>With several videos, the first paragraph of each has the video's name above it ("2 · part-2.mp4").</summary>
    public string? Video { get; init; }

    public bool HasVideo => Video is not null;

    /// <summary>"04:22".</summary>
    public string TimeText => TimeFormat.Clock(Start);

    public ObservableCollection<TranscriptWordViewModel> Words { get; } = [];

    [RelayCommand]
    private void Seek() => owner.SeekTo(Start);
}

public sealed class TranscriptScrollEventArgs(int wordIndex, bool center) : EventArgs
{
    public int WordIndex { get; } = wordIndex;

    /// <summary>Put the word in the middle, rather than only into view.</summary>
    public bool Center { get; } = center;
}
