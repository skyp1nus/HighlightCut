using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.Controls;
using HighlightCut.App.Demo;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;
using HighlightCut.Transcription.Models;

namespace HighlightCut.App.Tests;

/// <summary>
/// The transcript screens at the design size, saved to artifacts/screenshots/, and pointer input on the words
/// (the list and the timeline lane).
/// </summary>
public class TranscriptScreensTests
{
    private static MainWindow Show(EditorViewModel editor)
    {
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Save(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(1440, frame!.PixelSize.Width);
        frame.Save(Path.Combine(Screenshots.Directory, name + ".png"), new PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public void The_status_bar_shows_transcription_next_to_the_file_details()
    {
        var editor = App.CreateEditor(DesignScreen.Transcribing);
        var window = Show(editor);
        var bar = window.GetVisualDescendants().OfType<StatusBar>().Single();
        var details = bar.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == editor.StatusLeft);
        var item = bar.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == editor.TranscriptPanel.StatusText);

        double detailsRight = details.TranslatePoint(new Point(details.Bounds.Width, 0), bar)!.Value.X;
        double itemLeft = item.TranslatePoint(default, bar)!.Value.X;
        Assert.InRange(itemLeft - detailsRight, 16, 40);
        Assert.False(details.TextLayout.TextLines[0].HasCollapsed);
        window.Close();
    }

    [AvaloniaFact]
    public void A_selection_shows_its_bar()
    {
        var editor = App.CreateEditor(DesignScreen.Transcript);
        editor.TranscriptPanel.Select(271, 283);
        var window = Show(editor);
        Save(window, "transcript-selection");
        window.Close();
    }

    [AvaloniaFact]
    public void The_model_download_shows_its_progress()
    {
        var editor = App.CreateEditor(DesignScreen.NoModel);
        editor.TranscriptPanel.DownloadModelCommand.Execute(null);
        var window = Show(editor);
        // After the demo's download timer has had its chance to tick.
        editor.Settings.Models.Single(m => m.Id == ModelCatalog.Parakeet.Id).Progress = 0.64;
        Save(window, "no-model-downloading");
        window.Close();
    }

    [AvaloniaFact]
    public void Without_a_video_the_tab_asks_for_one()
    {
        var editor = App.CreateEditor(DesignScreen.Empty);
        editor.ShowTranscriptCommand.Execute(null);
        var window = Show(editor);
        Save(window, "transcript-no-video");
        window.Close();
    }

    private static Border WordBorder(Window window, int index) =>
        window.GetVisualDescendants().OfType<Border>().First(b => b.DataContext is TranscriptWordViewModel w && w.Index == index && b.Classes.Contains("word"));

    private static Point Middle(Visual visual, Window window) =>
        visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), window)!.Value;

    [AvaloniaFact]
    public void Dragging_across_words_selects_them()
    {
        var editor = App.CreateEditor(DesignScreen.Transcript);
        var window = Show(editor);

        window.MouseDown(Middle(WordBorder(window, 271), window), MouseButton.Left);
        window.MouseMove(Middle(WordBorder(window, 273), window));
        window.MouseMove(Middle(WordBorder(window, 275), window));
        window.MouseUp(Middle(WordBorder(window, 275), window), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(editor.TranscriptPanel.HasSelection);
        Assert.StartsWith("04:24.2 – ", editor.TranscriptPanel.SelectionText, StringComparison.Ordinal);
        Assert.EndsWith(" · 5 words", editor.TranscriptPanel.SelectionText, StringComparison.Ordinal);
        Assert.All(Enumerable.Range(271, 5), i => Assert.True(editor.TranscriptPanel.Words[i].IsSelected));
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_a_word_moves_the_playhead_to_it()
    {
        var editor = App.CreateEditor(DesignScreen.Transcript);
        var window = Show(editor);

        var point = Middle(WordBorder(window, 290), window);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        Assert.Equal(editor.TranscriptPanel.Words[290].Start, editor.Time, 6);
        Assert.False(editor.TranscriptPanel.HasSelection);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_a_lane_word_seeks()
    {
        var editor = App.CreateEditor(DesignScreen.Transcript);
        var window = Show(editor);
        var timeline = window.GetVisualDescendants().OfType<TimelineControl>().Single();
        // Lane words are packed from the start of their chunk: the chunk's first word starts 3 px in.
        var chunk = editor.TranscriptPanel.LaneChunks.First(c => c.FirstWord <= 280 && 280 < c.FirstWord + c.WordCount);
        var word = editor.TranscriptPanel.Words[chunk.FirstWord];
        double x = chunk.Start / editor.Duration * timeline.ExtentWidth - timeline.ScrollOffset + 5;
        Assert.InRange(x, 0, timeline.Bounds.Width);

        var point = timeline.TranslatePoint(new Point(x, TimelineControl.LaneTop + TimelineControl.LaneHeight / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        Assert.Equal(word.Start, editor.Time, 6);
        Assert.Equal(11, editor.Clips.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void The_transcript_follows_the_playhead_while_playing()
    {
        var editor = App.CreateEditor(DesignScreen.Transcript);
        var window = Show(editor);
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "Scroller");
        double before = scroller.Offset.Y;
        Assert.True(before > 0);

        editor.TogglePlay();
        editor.SetTime(editor.TranscriptPanel.Words[600].Start);
        Dispatcher.UIThread.RunJobs();
        editor.TogglePlay();
        Dispatcher.UIThread.RunJobs();

        Assert.True(scroller.Offset.Y > before + 200);
        window.Close();
    }
}
