using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using HighlightCut.App.Demo;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.Core.Model;
using HighlightCut.Core.Transcripts;
using HighlightCut.Transcription;
using HighlightCut.Transcription.Models;

namespace HighlightCut.App.Tests;

/// <summary>The Transcript tab and lane: the design's sample, word selection and its edits, search, and real files.</summary>
public sealed class TranscriptPanelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-transcript").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static TranscriptPanelViewModel Panel(EditorViewModel editor) => editor.TranscriptPanel;

    [Fact]
    public void The_design_transcript_matches_the_prototype()
    {
        var words = DesignTranscript.Full.Words;
        var paragraphs = TranscriptLayout.Paragraphs(words);
        var flags = TranscriptSearch.MarkFillers(words, FillerWords.All(FillerWords.Defaults), paragraphs);

        Assert.Equal(635, words.Count);
        Assert.Equal([0, 13, 60, 84, 135, 209, 219, 269, 303, 320, 339, 378, 385, 426, 451, 480, 514, 554, 585, 623],
            paragraphs.Select(p => p.FirstWord));
        Assert.Equal(123, TranscriptLayout.Chunks(words, paragraphs).Count);
        Assert.Equal([46, 73, 88, 209, 277, 292, 293, 313, 333, 419, 508, 560], Enumerable.Range(0, words.Count).Where(i => flags[i]));
        Assert.Equal([45, 271, 338, 536], TranscriptSearch.Find(words, "export").Select(m => m.First));
        Assert.Equal(("lossless", 267.427, 268.817), (words[274].Text, Math.Round(words[274].Start, 3), Math.Round(words[274].End, 3)));
    }

    [AvaloniaFact]
    public void The_transcript_screen_matches_the_design()
    {
        var editor = App.CreateEditor(DesignScreen.Transcript);
        var panel = Panel(editor);

        Assert.True(editor.IsTranscriptTab);
        Assert.Equal(TranscriptPanelState.Done, panel.State);
        Assert.Equal("English · parakeet v3", editor.SidebarHint);
        Assert.Equal([1, 2, 3, 31, 32, 33, 34, 41, 42, 43, 5], editor.Clips.Select(c => c.Id));
        Assert.Equal(["Export demo", "Export demo · 2", "Export demo · 5", "Quote · works offline", "Quote · why not a full editor"],
            editor.Clips.Where(c => c.Id is 3 or 31 or 34 or 41 or 43).Select(c => c.Label));
        Assert.Equal("1 of 4", panel.MatchText);
        Assert.Equal(274, panel.CurrentWordIndex);
        Assert.True(panel.Words[274].IsCurrent);
        Assert.True(panel.Words[45].IsCurrentMatch);
        Assert.True(panel.Words[271].IsMatch && !panel.Words[271].IsCurrentMatch);
        Assert.True(editor.ShowTranscriptLane && editor.IsTranscriptLaneVisible);
        Assert.Equal((232, 178), (editor.TimelineHeight, editor.TimelineTracksHeight));
        Assert.True(panel.Words[277].IsOut && panel.Words[277].IsFiller);
        Assert.False(panel.Words[274].IsOut);
        Assert.Equal(20, panel.Paragraphs.Count);
        Assert.Equal("04:22", panel.Paragraphs[7].TimeText);

        var log = editor.Claude.Log;
        Assert.Equal(["Added clip 00:12–00:45", "Labeled 5 clips from transcript", "Removed 4 filler words", "Kept 3 quotes"], log.Select(a => a.Text));
        Assert.Equal("um ×2, you know, uh in clip 3 “Export demo” · 5.3 s shorter", log[2].Meta);
        Assert.Equal([3, 31, 32, 33, 34], log[2].ClipIds);
        Assert.Equal("Replaced “Q&A highlights” with clips 8–10: works offline, six-hour stream, why not a full editor", log[3].Meta);
        Assert.Equal([4, 41, 42, 43], log[3].ClipIds);
        Assert.Equal("Idle · 4 actions", editor.Claude.StatusLine);
    }

    [AvaloniaFact]
    public void The_claude_cards_undo_the_transcript_edits()
    {
        var editor = App.CreateEditor(DesignScreen.Transcript);

        editor.Claude.Log[3].ToggleUndo();
        Assert.Equal([1, 2, 3, 31, 32, 33, 34, 4, 5], editor.Clips.Select(c => c.Id));
        editor.Claude.Log[2].ToggleUndo();
        Assert.Equal([1, 2, 3, 4, 5], editor.Clips.Select(c => c.Id));
        Assert.Equal((262.08, 365.52), (editor.Find(3)!.Start, editor.Find(3)!.End));
    }

    [AvaloniaFact]
    public void Other_screens_have_the_transcript_on_the_clips_tab()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);

        Assert.True(editor.IsClipsTab);
        Assert.Equal("Drag to reorder", editor.SidebarHint);
        Assert.False(editor.ShowTranscriptLane);
        Assert.Equal(TranscriptPanelState.Done, Panel(editor).State);
        Assert.Equal(635, Panel(editor).Words.Count);

        editor.ShowTranscriptCommand.Execute(null);
        Assert.Equal("English · parakeet v3", editor.SidebarHint);
    }

    [AvaloniaFact]
    public void Clicking_a_word_moves_the_playhead_and_clears_the_selection()
    {
        var editor = App.CreateEditor(DesignScreen.Transcript);
        var panel = Panel(editor);
        panel.Select(271, 273);

        panel.SeekToWord(290);

        Assert.Equal(panel.Words[290].Start, editor.Time);
        Assert.Equal(290, panel.CurrentWordIndex);
        Assert.False(panel.HasSelection);
        Assert.DoesNotContain(panel.Words, w => w.IsSelected);
    }

    [AvaloniaFact]
    public void Selecting_words_shows_their_range()
    {
        var panel = Panel(App.CreateEditor(DesignScreen.Transcript));

        panel.Select(273, 271);
        Assert.True(panel.HasSelection);
        Assert.Equal("04:24.2 – 04:27.3 · 3.1 s · 3 words", panel.SelectionText);
        Assert.Equal([271, 272, 273], panel.Words.Where(w => w.IsSelected).Select(w => w.Index));

        panel.Select(271, 283);
        Assert.Equal("04:24.2 – 04:39.2 · 14.9 s · 13 words", panel.SelectionText);

        panel.Select(280, 280);
        Assert.EndsWith(" · 1 word", panel.SelectionText, StringComparison.Ordinal);

        panel.ClearSelectionCommand.Execute(null);
        Assert.False(panel.HasSelection);
        Assert.Equal("", panel.SelectionText);
    }

    [AvaloniaFact]
    public void Keep_as_clip_adds_a_labelled_clip_in_source_order()
    {
        var editor = App.CreateEditor(DesignScreen.Transcript);
        var panel = Panel(editor);
        // Three words in the gap between clip 1 and clip 2.
        int first = panel.Words.ToList().FindIndex(w => w.Start > editor.Find(1)!.End + 1);
        Assert.True(panel.Words[first + 2].End < editor.Find(2)!.Start - 1);
        panel.Select(first, first + 2);

        panel.KeepSelectionCommand.Execute(null);

        var clip = editor.SelectedClip!;
        Assert.NotEqual(2, clip.Id);
        Assert.False(string.IsNullOrWhiteSpace(clip.Label));
        Assert.True(clip.Start < panel.Words[first].Start && clip.Start > editor.Find(1)!.End);
        Assert.Equal(panel.Words[first + 2].End + 0.1, clip.End, 6);
        Assert.Equal(editor.Clips.IndexOf(editor.Find(1)!) + 1, editor.Clips.IndexOf(clip));
        Assert.False(panel.HasSelection);

        editor.Undo();
        Assert.Equal(11, editor.Clips.Count);
    }

    [AvaloniaFact]
    public void Keep_as_clip_does_not_duplicate_words_a_clip_already_has()
    {
        var editor = App.CreateEditor(DesignScreen.Transcript);
        var panel = Panel(editor);
        double output = editor.OutputDuration;
        // "export This is": inside clip 3.
        panel.Select(271, 273);

        panel.KeepSelectionCommand.Execute(null);

        Assert.Equal(11, editor.Clips.Count);
        Assert.Equal(output, editor.OutputDuration, 6);
        Assert.Contains("already in clip", editor.StatusMessage);
        Assert.Empty(editor.Session.Project.Overlaps());
    }

    [AvaloniaFact]
    public void Cut_out_splits_the_clip_in_one_undo_step()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        var panel = Panel(editor);
        panel.Select(271, 273);
        Assert.False(panel.Words[272].IsOut);

        panel.CutSelectionCommand.Execute(null);

        Assert.Equal(6, editor.Clips.Count);
        Assert.All(Enumerable.Range(271, 3), i => Assert.True(panel.Words[i].IsOut));
        Assert.False(panel.Words[270].IsOut);
        Assert.False(panel.Words[274].IsOut);
        Assert.False(panel.HasSelection);
        Assert.Equal("Cut out “export This is”", editor.Session.History.Entries[^1].Description);

        editor.Undo();
        Assert.Equal(5, editor.Clips.Count);
        Assert.False(panel.Words[272].IsOut);
    }

    [AvaloniaFact]
    public void Cutting_words_outside_the_output_says_so()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        var panel = Panel(editor);
        panel.Select(3, 12);

        panel.CutSelectionCommand.Execute(null);

        Assert.Equal("Those words are not in the output.", editor.StatusMessage);
        Assert.Equal(5, editor.Clips.Count);
        Assert.True(panel.HasSelection);
    }

    [AvaloniaFact]
    public async Task Cut_out_with_no_clips_keeps_the_rest_of_the_video()
    {
        var editor = await OpenSampleAsync();
        ((DesignSample)editor.Media!).SetTranscript(TranscriptState.Done);
        var panel = Panel(editor);
        Assert.Empty(editor.Clips);
        Assert.All(panel.Words, w => Assert.False(w.IsOut));

        panel.Select(271, 280);
        panel.CutSelectionCommand.Execute(null);

        Assert.Equal(2, editor.Clips.Count);
        Assert.Equal(0, editor.Clips[0].Start);
        Assert.Equal(editor.Duration, editor.Clips[1].End);
        Assert.Equal("Cut out “export This is lossless…”", editor.Session.History.Entries[^1].Description);
        Assert.False(panel.Words[100].IsOut);
        Assert.True(panel.Words[275].IsOut);

        editor.Undo();
        Assert.Empty(editor.Clips);
        Assert.All(panel.Words, w => Assert.False(w.IsOut));
    }

    [AvaloniaFact]
    public async Task Words_are_struck_only_once_something_is_marked()
    {
        var editor = await OpenSampleAsync();
        ((DesignSample)editor.Media!).SetTranscript(TranscriptState.Done);
        var panel = Panel(editor);
        Assert.Empty(editor.Clips);
        Assert.All(panel.Words, w => Assert.False(w.IsOut));

        panel.Select(271, 280);
        panel.KeepSelectionCommand.Execute(null);

        Assert.Single(editor.Clips);
        Assert.False(panel.Words[275].IsOut);
        Assert.True(panel.Words[100].IsOut);
        Assert.True(panel.Words[400].IsOut);

        editor.RemoveClip(editor.Clips[0]);
        Assert.Empty(editor.Clips);
        Assert.All(panel.Words, w => Assert.False(w.IsOut));

        editor.Undo();
        Assert.Single(editor.Clips);
        Assert.True(panel.Words[100].IsOut);

        editor.Redo();
        Assert.Empty(editor.Clips);
        Assert.All(panel.Words, w => Assert.False(w.IsOut));
    }

    [AvaloniaFact]
    public async Task Play_selection_stops_at_the_end()
    {
        var editor = App.CreateEditor(DesignScreen.Transcript);
        var panel = Panel(editor);
        panel.Select(271, 273);
        double end = panel.Words[273].End;
        editor.Speed = 2;

        panel.PlaySelectionCommand.Execute(null);
        Assert.True(editor.IsPlaying);
        Assert.Equal(panel.Words[271].Start, editor.Time);

        // The design's sample plays on a timer.
        for (int i = 0; i < 200 && editor.IsPlaying; i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
        Dispatcher.UIThread.RunJobs();

        Assert.False(editor.IsPlaying);
        Assert.Equal(end + 0.1, editor.Time, 6);
    }

    [AvaloniaFact]
    public void A_seek_during_play_selection_is_kept()
    {
        var editor = App.CreateEditor(DesignScreen.Transcript);
        var panel = Panel(editor);
        panel.Select(271, 273);

        panel.PlaySelectionCommand.Execute(null);
        editor.SetTime(300);
        Dispatcher.UIThread.RunJobs();

        Assert.True(editor.IsPlaying);
        Assert.InRange(editor.Time, 300, 301);
        editor.TogglePlay();
    }

    [AvaloniaFact]
    public void Search_steps_through_matches_and_wraps()
    {
        var editor = App.CreateEditor(DesignScreen.Transcript);
        var panel = Panel(editor);

        panel.NextMatchCommand.Execute(null);
        Assert.Equal("2 of 4", panel.MatchText);
        Assert.Equal(panel.Words[271].Start, editor.Time);
        Assert.True(panel.Words[271].IsCurrentMatch);

        panel.PreviousMatchCommand.Execute(null);
        panel.PreviousMatchCommand.Execute(null);
        Assert.Equal("4 of 4", panel.MatchText);
        Assert.Equal(panel.Words[536].Start, editor.Time);
        panel.NextMatchCommand.Execute(null);
        Assert.Equal("1 of 4", panel.MatchText);

        panel.Query = "zzz";
        Assert.Equal("No match", panel.MatchText);
        Assert.False(panel.NextMatchCommand.CanExecute(null));
        Assert.DoesNotContain(panel.Words, w => w.IsMatch);

        panel.Query = "you know";
        Assert.Equal("1 of 1", panel.MatchText);
        Assert.True(panel.Words[292].IsCurrentMatch && panel.Words[293].IsCurrentMatch);

        panel.Query = " ";
        Assert.Equal("", panel.MatchText);
    }

    [AvaloniaFact]
    public void The_transcribing_screen_fills_in()
    {
        var editor = App.CreateEditor(DesignScreen.Transcribing);
        var panel = Panel(editor);

        Assert.Equal(TranscriptPanelState.Running, panel.State);
        Assert.Equal(0.34, panel.Progress, 6);
        Assert.Equal(299, panel.Words.Count);
        Assert.Equal("a", panel.Words[^1].Text);
        Assert.Equal(8, panel.Paragraphs.Count);
        Assert.Equal("Transcribing · 04:57 of 14:32", panel.TailText);
        Assert.Equal("Transcribing 34%", editor.SidebarHint);
        Assert.True(panel.IsStatusVisible);
        Assert.Equal("transcribing 34% · parakeet · GPU", panel.StatusText);
        Assert.Null(panel.LaneMessage);
        Assert.Equal(269, panel.CurrentWordIndex);
        Assert.True(editor.IsTranscriptLaneVisible);
        var first = panel.Words.ToList();
        int version = panel.LayoutVersion;

        var sample = (DesignSample)editor.Media!;
        for (int i = 0; i < 132; i++)
            sample.TickTranscription();

        Assert.Equal(TranscriptPanelState.Done, panel.State);
        Assert.Equal("English · parakeet v3", editor.SidebarHint);
        Assert.False(panel.IsStatusVisible);
        Assert.Equal(635, panel.Words.Count);
        Assert.Equal(20, panel.Paragraphs.Count);
        Assert.Equal(version, panel.LayoutVersion);
        Assert.All(first, w => Assert.Same(w, panel.Words[w.Index]));
    }

    [AvaloniaFact]
    public void The_no_model_screen_downloads_parakeet_and_then_transcribes()
    {
        var editor = App.CreateEditor(DesignScreen.NoModel);
        var panel = Panel(editor);

        Assert.True(editor.IsTranscriptTab);
        Assert.False(editor.ShowTranscriptLane);
        Assert.Equal(TranscriptPanelState.NoModel, panel.State);
        Assert.Equal("", editor.SidebarHint);
        Assert.True(panel.ShowDownloadButton);
        Assert.Equal("Download parakeet-tdt-0.6b-v3 (1.3 GB)", panel.DownloadButtonText);
        Assert.Equal("No transcript yet · install a model in the Transcript tab", panel.LaneMessage);

        panel.DownloadModelCommand.Execute(null);
        Assert.True(panel.IsDownloadingModel);
        Assert.False(panel.ShowDownloadButton);
        Assert.Equal("Downloading the transcription model…", panel.LaneMessage);
        Assert.Matches(new Regex(@"^\d\.\d of 1\.3 GB · 48 MB/s$"), panel.DownloadDetail);

        for (int i = 0; i < 400 && panel.IsDownloadingModel; i++)
            editor.Settings.TickDownloads();

        Assert.True(panel.SuggestedModel!.IsInstalled);
        Assert.Equal(TranscriptPanelState.Running, panel.State);
        Assert.Equal("Transcribing 0%", editor.SidebarHint);
    }

    [AvaloniaFact]
    public void A_cancelled_download_offers_it_again()
    {
        var editor = App.CreateEditor(DesignScreen.NoModel);
        var panel = Panel(editor);
        panel.DownloadModelCommand.Execute(null);

        panel.CancelDownloadCommand.Execute(null);

        Assert.False(panel.IsDownloadingModel);
        Assert.True(panel.ShowDownloadButton);
        Assert.Equal(TranscriptPanelState.NoModel, panel.State);
    }

    [AvaloniaFact]
    public void A_failed_or_too_big_download_says_why()
    {
        var editor = App.CreateEditor(DesignScreen.NoModel);
        var panel = Panel(editor);
        var parakeet = panel.SuggestedModel!;
        Assert.Equal("Multilingual, includes Ukrainian", panel.ModelNote);

        parakeet.Error = "Could not download: 404";
        parakeet.State = ModelState.Failed;
        Assert.True(panel.ShowDownloadButton);
        Assert.Equal("Retry download (1.3 GB)", panel.DownloadButtonText);
        Assert.Equal("Could not download: 404", panel.ModelNote);

        parakeet.SpaceNote = "Needs 1.3 GB · 100 MB free on D:";
        parakeet.State = ModelState.NoSpace;
        Assert.False(panel.ShowDownloadButton);
        Assert.True(panel.ShowModelNote);
        Assert.Equal("Needs 1.3 GB · 100 MB free on D:", panel.ModelNote);
        Assert.Equal("Not enough space for the transcription model · see the Transcript tab", panel.LaneMessage);
    }

    [AvaloniaFact]
    public async Task Downloading_from_the_tab_transcribes_the_video_even_when_not_on_open()
    {
        var parakeet = ModelCatalog.Parakeet;
        byte[] archive = SettingsViewModelTests.Archive("sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", parakeet.Files);
        var preview = new ScriptedPreview();
        var editor = new EditorViewModel { MediaOpener = new ScriptedOpener(preview) };
        editor.Settings.ModelsFolder = Directory.CreateDirectory(Path.Combine(_dir, "no-models")).FullName;
        editor.Settings.Installer = new ModelInstaller(new HttpClient(SettingsViewModelTests.FakeServer.Serving(archive)));
        editor.Settings.TranscribeOnOpen = false;
        await editor.OpenMediaAsync(Path.Combine(_dir, "talk.mp4"));
        var panel = Panel(editor);
        Assert.Equal(TranscriptPanelState.NoModel, panel.State);
        preview.Started = 0;

        panel.DownloadModelCommand.Execute(null);
        for (int i = 0; i < 250 && !panel.SuggestedModel!.IsInstalled; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(panel.SuggestedModel!.IsInstalled);
        Assert.Equal(1, preview.Started);
    }

    [AvaloniaFact]
    public void The_model_link_opens_the_transcription_settings()
    {
        var editor = App.CreateEditor(DesignScreen.NoModel);
        editor.Settings.Section = "General";

        Panel(editor).OpenModelSettingsCommand.Execute(null);

        Assert.True(editor.Settings.IsOpen);
        Assert.True(editor.Settings.IsTranscription);
    }

    [AvaloniaFact]
    public void Without_a_video_the_tab_asks_for_one()
    {
        var editor = App.CreateEditor(DesignScreen.Empty);

        editor.ShowTranscriptCommand.Execute(null);

        Assert.Equal(TranscriptPanelState.NoVideo, Panel(editor).State);
        Assert.Equal("", editor.SidebarHint);
        Assert.Null(Panel(editor).LaneMessage);
        Assert.Empty(Panel(editor).Paragraphs);
    }

    [AvaloniaFact]
    public void Transcript_lane_changes_the_timeline_height()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        Assert.Equal((212, 158), (editor.TimelineHeight, editor.TimelineTracksHeight));

        editor.ToggleTranscriptLaneCommand.Execute(null);
        Assert.True(editor.IsTranscriptLaneVisible);
        Assert.Equal((232, 178), (editor.TimelineHeight, editor.TimelineTracksHeight));

        editor.ToggleTranscriptLaneCommand.Execute(null);
        Assert.Equal((212, 158), (editor.TimelineHeight, editor.TimelineTracksHeight));

        var empty = App.CreateEditor(DesignScreen.Empty);
        empty.ShowTranscriptLane = true;
        Assert.False(empty.IsTranscriptLaneVisible);
        Assert.Equal(212, empty.TimelineHeight);
    }

    [AvaloniaFact]
    public void Changing_the_filler_list_remarks_words()
    {
        var editor = App.CreateEditor(DesignScreen.Transcript);
        var panel = Panel(editor);
        Assert.True(panel.Words[73].IsFiller);
        Assert.False(panel.Words[47].IsFiller);

        editor.Settings.SetFillerWords("en", ["um", "uh", "no timeline"]);

        Assert.False(panel.Words[73].IsFiller);
        Assert.True(panel.Words[46].IsFiller);
        Assert.True(panel.Words[47].IsFiller && panel.Words[48].IsFiller);
        Assert.Contains("no timeline", panel.FillerWords);
    }

    [AvaloniaFact]
    public void Real_download_reports_bytes()
    {
        var editor = new EditorViewModel();
        var panel = Panel(editor);
        var model = panel.SuggestedModel!;

        model.ReportBytes(InstallPhase.Downloading, 212_000_000, 488_000_000);
        Assert.Equal("212 of 488 MB", panel.DownloadDetail);

        model.ReportBytes(InstallPhase.Downloading, 1_200_000, null);
        Assert.Equal("1 of 487 MB", panel.DownloadDetail);

        model.ReportBytes(InstallPhase.Unpacking, 488_000_000, 488_000_000);
        Assert.Equal("Unpacking…", panel.DownloadDetail);
    }

    [AvaloniaFact]
    public async Task Real_files_follow_the_preview_transcript()
    {
        var preview = new ScriptedPreview();
        var editor = new EditorViewModel { MediaOpener = new ScriptedOpener(preview) };
        editor.Settings.ModelsFolder = Directory.CreateDirectory(Path.Combine(_dir, "no-models")).FullName;
        var panel = Panel(editor);
        Assert.Equal(TranscriptPanelState.NoVideo, panel.State);

        await editor.OpenMediaAsync(Path.Combine(_dir, "talk.mp4"));
        Assert.Equal(TranscriptPanelState.NoModel, panel.State);
        Assert.False(panel.SuggestedModel!.CanManage);

        editor.Settings.ModelsFolder = FakeModels(ModelCatalog.Parakeet);
        Assert.Equal(TranscriptPanelState.NotStarted, panel.State);
        Assert.Equal("No transcript yet · transcribe it in the Transcript tab", panel.LaneMessage);
        preview.Started = 0;
        panel.TranscribeCommand.Execute(null);
        Assert.Equal(1, preview.Started);

        preview.State = TranscriptState.Waiting;
        preview.Raise();
        Assert.Equal(TranscriptPanelState.Waiting, panel.State);
        Assert.True(panel.HasText && panel.IsRunning);
        Assert.Equal("Waiting for the analysis to finish…", panel.TailText);
        Assert.False(panel.IsStatusVisible);

        Word[] words = [new("Hello", 1, 1.4), new("there.", 1.5, 2), new("Next", 5, 5.4), new("part", 5.5, 6)];
        preview.State = TranscriptState.Running;
        preview.Progress = 0.5;
        preview.Transcript = new Transcript("parakeet-tdt-0.6b-v3", "auto", words[..2]);
        preview.Raise();
        Assert.Equal(TranscriptPanelState.Running, panel.State);
        Assert.True(panel.IsStatusVisible);
        Assert.Equal("transcribing 50% · parakeet · CPU", panel.StatusText);
        Assert.DoesNotContain("transcribing", editor.StatusLeft, StringComparison.Ordinal);
        Assert.Contains("analysing 20%", editor.StatusLeft, StringComparison.Ordinal);
        Assert.Single(panel.Paragraphs);
        var hello = panel.Words[0];

        preview.Transcript = new Transcript("parakeet-tdt-0.6b-v3", "auto", words);
        preview.Raise();
        Assert.Equal(2, panel.Paragraphs.Count);
        Assert.Same(hello, panel.Words[0]);

        preview.State = TranscriptState.Done;
        preview.Transcript = preview.Transcript with { ApproximateTimes = true };
        preview.Raise();
        Assert.Equal("parakeet v3", panel.Hint);
        Assert.Equal("Word times are estimated for this model", panel.HintTip);
        Assert.Same(hello, panel.Words[0]);

        preview.State = TranscriptState.Failed;
        preview.Error = "The model could not be loaded.";
        preview.Transcript = null;
        preview.Raise();
        Assert.Equal(TranscriptPanelState.Failed, panel.State);
        Assert.Equal("The model could not be loaded.", panel.ErrorText);
        Assert.Equal("Transcription failed · see the Transcript tab", panel.LaneMessage);
        Assert.Empty(panel.Paragraphs);

        preview.State = TranscriptState.Done;
        preview.Transcript = new Transcript("whisper-small", "uk", []);
        preview.Raise();
        Assert.True(panel.IsEmptyTranscript);
        Assert.Equal("Ukrainian · whisper small", panel.Hint);
    }

    private async Task<EditorViewModel> OpenSampleAsync()
    {
        var editor = new EditorViewModel { MediaOpener = new SampleOpener() };
        editor.Settings.ModelsFolder = Directory.CreateDirectory(Path.Combine(_dir, "no-models")).FullName;
        await editor.OpenMediaAsync(Path.Combine(_dir, "interview.mp4"));
        return editor;
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

    /// <summary>A file whose transcription the test moves along by hand.</summary>
    private sealed class ScriptedPreview : IMediaPreview
    {
        public Transcript? Transcript { get; set; }
        public TranscriptState State { get; set; }
        public double Progress { get; set; }
        public string? Error { get; set; }
        public int Started { get; set; }

        Transcript? IMediaPreview.Transcript => Transcript;
        TranscriptState IMediaPreview.TranscriptState => State;
        double IMediaPreview.TranscriptProgress => Progress;
        string? IMediaPreview.TranscriptError => Error;

        public double Duration => 60;
        public double FrameRate => 25;
        public IReadOnlyList<double> Keyframes => [];
        public int AudioStreamCount => 1;
        public bool IsPlaceholder => false;
        public bool IsPlayable => false;
        public string? Activity => State == TranscriptState.Running ? "analysing 20% · transcribing 50%" : null;
        public string? AnalysisError => null;

        public event EventHandler? Changed;

        public void StartTranscription(TranscriptionSetup setup) => Started++;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public double AudioPeak(int stream, double startTime, double endTime) => 0.5;

        public void DrawFrame(DrawingContext context, Rect rect, double time, FrameLook look, int variant) =>
            context.FillRectangle(Brushes.DimGray, rect);
    }

    private sealed class ScriptedOpener(ScriptedPreview preview) : IMediaOpener
    {
        public Task<OpenedMedia> OpenAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenedMedia(new SourceMedia(path, 60, 25, [new AudioTrack(1, "Mic")]), preview, "talk.mp4"));
    }
}
