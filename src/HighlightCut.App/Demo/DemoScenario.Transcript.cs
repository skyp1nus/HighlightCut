using System.ComponentModel;
using System.Globalization;
using HighlightCut.App.ViewModels;
using HighlightCut.Core.Editing;
using HighlightCut.Core.Editing.Commands;
using HighlightCut.Core.Transcripts;
using HighlightCut.Transcription;
using HighlightCut.Transcription.Models;

namespace HighlightCut.App.Demo;

/// <summary>The transcript screens (prototype views <c>transcript</c>, <c>transcribing</c> and <c>nomodel</c>).</summary>
public static partial class DemoScenario
{
    /// <summary>Width of the design's timeline (1440 − 44 px track names − 12 px margin), for its zoom factors.</summary>
    private const double DesignTimelineWidth = 1384;

    static partial void ApplyTranscript(EditorViewModel editor, DesignScreen screen)
    {
        editor.Tab = SidebarTab.Clips;
        editor.ShowTranscriptLane = false;
        if (editor.Media is not DesignSample sample)
            return;
        switch (screen)
        {
            case DesignScreen.Transcript:
                sample.SetTranscript(TranscriptState.Done);
                AddTranscriptEdits(editor);
                editor.Select(null);
                editor.Tab = SidebarTab.Transcript;
                editor.ShowTranscriptLane = true;
                editor.ZoomLevel = ZoomLevelFor(10, editor);
                editor.Time = 268.9;
                editor.TranscriptPanel.Query = "export";
                editor.Claude.Recount();
                editor.ApplyClaudeHighlights();
                // The prototype also starts playback here; the demo stays paused so screenshots are stable.
                break;
            case DesignScreen.Transcribing:
                editor.Select(null);
                editor.Tab = SidebarTab.Transcript;
                editor.ShowTranscriptLane = true;
                editor.ZoomLevel = ZoomLevelFor(6, editor);
                editor.Time = 262.4;
                sample.StartTranscribing(0.34);
                break;
            case DesignScreen.NoModel:
                editor.Select(null);
                editor.Tab = SidebarTab.Transcript;
                editor.Settings.LoadDesignNoModels();
                sample.SetTranscript(TranscriptState.None);
                TranscribeWhenInstalled(editor, sample);
                break;
            default:
                sample.SetTranscript(TranscriptState.Done);
                break;
        }
    }

    /// <summary>The prototype's download ends in transcription: once Parakeet is installed the sample starts transcribing.</summary>
    private static void TranscribeWhenInstalled(EditorViewModel editor, DesignSample sample)
    {
        var parakeet = editor.Settings.Models.First(m => m.Id == ModelCatalog.Parakeet.Id);
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(TranscriptionModelViewModel.State) || !parakeet.IsInstalled)
                return;
            parakeet.PropertyChanged -= OnChanged;
            if (ReferenceEquals(editor.Media, sample))
                sample.StartTranscribing(0);
        }
        parakeet.PropertyChanged += OnChanged;
    }

    /// <summary>A zoom factor as a slider position, as the timeline maps it at the design's width.</summary>
    private static double ZoomLevelFor(double factor, EditorViewModel editor) =>
        Math.Log(factor) / Math.Log(Math.Max(8, editor.Duration * editor.FrameRate * 12 / DesignTimelineWidth));

    /// <summary>
    /// Claude's transcript edits before the "Editing with transcript" screen (prototype <c>transcriptSetup()</c>):
    /// the filler words cut out of clip 3, and "Q&amp;A highlights" replaced by three quotes.
    /// </summary>
    private static void AddTranscriptEdits(EditorViewModel editor)
    {
        var claude = editor.Claude;
        var session = editor.Session;
        var now = DateTimeOffset.Now;
        var words = DesignTranscript.Full.Words;
        var paragraphs = TranscriptLayout.Paragraphs(words);
        var flags = TranscriptSearch.MarkFillers(words, FillerWords.All(FillerWords.Defaults), paragraphs);

        var clip3 = session.Project.Get(3);
        var groups = TranscriptSearch.Groups(flags, paragraphs)
            .Select(g => (Start: words[g.First].Start, End: words[g.First + g.Count - 1].End,
                Text: string.Join(' ', words.Skip(g.First).Take(g.Count).Select(w => TranscriptSearch.Normalize(w.Text)))))
            .Where(g => g.Start >= clip3.Start && g.End <= clip3.End)
            .ToList();
        var parts = new List<(double Start, double End)>();
        double from = clip3.Start;
        foreach (var g in groups)
        {
            parts.Add((from, g.Start - 0.06));
            from = g.End + 0.1;
        }
        parts.Add((from, clip3.End));

        int at = session.Project.IndexOf(3);
        var cuts = new List<IEditCommand> { new SetClipRangeCommand(3, parts[0].Start, parts[0].End) };
        for (int k = 1; k < parts.Count; k++)
        {
            cuts.Add(new AddClipCommand(parts[k].Start, parts[k].End, string.Create(CultureInfo.InvariantCulture, $"{clip3.Label} · {k + 1}"),
                at + k, 30 + k));
        }
        string title = string.Create(CultureInfo.InvariantCulture, $"Removed {groups.Count} filler words");
        var fillerEntry = session.Execute(new BatchCommand("cut_filler_words", title, cuts), EditOrigin.Assistant)!;
        string tally = string.Join(", ", groups.GroupBy(g => g.Text).Select(t => t.Count() > 1
            ? string.Create(CultureInfo.InvariantCulture, $"{t.Key} ×{t.Count()}")
            : t.Key));
        double saved = groups.Sum(g => g.End + 0.1 - (g.Start - 0.06));
        claude.Add(new(ClaudeLogKind.Action, title,
            string.Create(CultureInfo.InvariantCulture, $"{tally} in clip 3 “{clip3.Label}” · {saved:0.0} s shorter"),
            [3, .. Enumerable.Range(1, parts.Count - 1).Select(k => 30 + k)], fillerEntry.OutputDelta,
            setUndone: Safe(editor, undone => Toggle(session, fillerEntry, undone)), at: now.AddMinutes(-9)));

        (int Paragraph, string Label)[] quotes =
            [(13, "Quote · works offline"), (14, "Quote · six-hour stream"), (15, "Quote · why not a full editor")];
        int q = session.Project.IndexOf(4);
        var keep = new List<IEditCommand> { new RemoveClipCommand(4) };
        for (int k = 0; k < quotes.Length; k++)
        {
            var (start, end, _) = DesignTranscript.Paragraphs[quotes[k].Paragraph];
            keep.Add(new AddClipCommand(start - 0.3, end + 0.3, quotes[k].Label, q + k, 41 + k));
        }
        var quoteEntry = session.Execute(new BatchCommand("keep_quotes", "Kept 3 quotes", keep), EditOrigin.Assistant)!;
        int first = session.Project.NumberOf(41);
        claude.Add(new(ClaudeLogKind.Action, "Kept 3 quotes",
            string.Create(CultureInfo.InvariantCulture,
                $"Replaced “Q&A highlights” with clips {first}–{first + 2}: works offline, six-hour stream, why not a full editor"),
            [4, 41, 42, 43], quoteEntry.OutputDelta,
            setUndone: Safe(editor, undone => Toggle(session, quoteEntry, undone)), at: now.AddMinutes(-6)));
    }
}
