using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.Controls;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;
using HighlightCut.Core.Model;

namespace HighlightCut.App.Tests;

/// <summary>The processing screen over the editor while a file is prepared, and its estimate of the time left.</summary>
public sealed class ProcessingScreenTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Time_left_follows_the_recent_rate_and_counts_down_smoothly()
    {
        var estimator = new TimeLeftEstimator();
        Assert.Null(estimator.Add(0, 0));
        Assert.Null(estimator.Add(0.3, 0.03));

        // 10 % a second: 9 s left at 10 %.
        Assert.Equal(9, estimator.Add(1, 0.1)!.Value, 1);
        // Stalled for a second: the rate says 18 s now; the estimate grows only a third of the way there.
        double stalled = estimator.Add(2, 0.1)!.Value;
        Assert.Equal(8 * 2 / 3.0 + 18 / 3.0, stalled, 6);
        // Much faster again: it comes down, but no further than a third of the way at once.
        double next = estimator.Add(3, 0.5)!.Value;
        Assert.Equal((stalled - 1) * 2 / 3 + 3 / 3.0, next, 6);
        Assert.Equal(0, estimator.Add(4, 1));

        estimator.Reset();
        Assert.Null(estimator.Add(10, 0.5));
    }

    [Theory]
    [InlineData(null, "estimating time left")]
    [InlineData(1.2, "almost done")]
    [InlineData(7.2, "about 8 s left")]
    [InlineData(21.0, "about 25 s left")]
    [InlineData(59.0, "about 60 s left")]
    [InlineData(61.0, "about 2 min left")]
    [InlineData(600.0, "about 10 min left")]
    public void Time_left_reads_in_rounded_words(double? seconds, string text) =>
        Assert.Equal(text, TimeLeftEstimator.Format(seconds));

    [Theory]
    [InlineData(3734, "keynote.mp4 · 4K · 29.97 fps", "1:02:14 · 4K · 29.97 fps")]
    [InlineData(134.4, "talk.mp4 · 1080p · 30 fps", "2:14 · 1080p · 30 fps")]
    [InlineData(5, "voice.m4a", "0:05")]
    public void Details_are_the_length_then_the_picture(double duration, string info, string details) =>
        Assert.Equal(details, ProcessingViewModel.DetailsFor(duration, info));

    [AvaloniaFact]
    public async Task A_quick_analysis_never_shows_the_screen()
    {
        var processing = new ProcessingViewModel(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(50));
        var media = new AnalysingPreview();

        processing.Track(media, "talk.mp4", "talk.mp4 · 1080p · 30 fps", 60);
        media.Finish();
        processing.Update();
        await Pump(300);

        Assert.False(processing.IsVisible);
    }

    [AvaloniaFact]
    public async Task A_longer_analysis_fades_in_says_what_is_left_and_fades_out()
    {
        var processing = new ProcessingViewModel(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        var media = new AnalysingPreview { Progress = 0.1, Stage = "Finding keyframes" };

        processing.Track(media, "interview-final.mp4", "interview-final.mp4 · 1080p · 30 fps", 3734);
        Assert.False(processing.IsVisible);
        await Pump(200);

        Assert.True(processing.IsVisible);
        Assert.True(processing.IsShown);
        Assert.Equal("interview-final.mp4", processing.FileName);
        Assert.Equal("1:02:14 · 1080p · 30 fps", processing.Details);
        Assert.Equal("Finding keyframes · 10% · estimating time left", processing.Status);

        await Task.Delay(600, Ct);
        media.Progress = 0.72;
        processing.Update();
        Assert.Matches(@"^Finding keyframes · 72% · (about \d+ s left|almost done)$", processing.Status);
        Assert.Equal(0.72 * ProcessingViewModel.ProgressLength, processing.ProgressWidth, 6);

        media.Finish();
        processing.Update();
        Assert.True(processing.IsLeaving);
        Assert.False(processing.IsShown);
        Assert.True(processing.IsVisible);
        await Pump(250);
        Assert.False(processing.IsVisible);
        Assert.False(processing.IsLeaving);
    }

    [AvaloniaFact]
    public async Task The_screen_covers_the_editor_below_the_title_bar()
    {
        var media = new AnalysingPreview { Progress = 0.72, Stage = "Finding keyframes" };
        var editor = App.CreateEditor(null, new AnalysingOpener(media));
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        var overlay = window.GetVisualDescendants().OfType<ProcessingOverlay>().Single();
        Assert.False(overlay.IsVisible);

        await editor.OpenMediaAsync("/videos/interview-final.mp4");
        await Pump(700);

        Assert.True(overlay.IsVisible);
        Assert.Equal(48, overlay.TranslatePoint(new Point(0, 0), window)!.Value.Y);
        Assert.True(overlay.GetVisualDescendants().OfType<BlobView>().Single().IsPlaying);
        Assert.Contains(overlay.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "interview-final.mp4");
        for (int i = 0; i < 12; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Pump(60);
        }
        using (var frame = window.CaptureRenderedFrame())
            frame!.Save(Path.Combine(Screenshots.Directory, "processing.png"), new PngBitmapEncoderOptions());

        media.Finish();
        await Pump(900);
        Assert.False(overlay.IsVisible);
        Assert.False(overlay.GetVisualDescendants().OfType<BlobView>().Single().IsPlaying);
        window.Close();
    }

    [AvaloniaFact]
    public void The_blob_is_never_a_circle_and_keeps_its_size()
    {
        var center = new Point(75, 75);
        var a = BlobView.Outline(center, 46, 0).Bounds;
        var b = BlobView.Outline(center, 46, 2.5).Bounds;

        Assert.NotEqual(a, b);
        foreach (var bounds in new[] { a, b })
        {
            Assert.InRange(bounds.Width, 80, 112);
            Assert.InRange(bounds.Height, 80, 112);
        }
    }

    private static async Task Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15, Ct);
        }
        while (DateTime.UtcNow < until);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A real-looking file whose analysis the test moves along.</summary>
    private sealed class AnalysingPreview : IMediaPreview
    {
        private bool _analysing = true;

        public double Progress { get; set; }
        public string? Stage { get; set; } = "Making thumbnails";

        public void Finish()
        {
            _analysing = false;
            Progress = 1;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        bool IMediaPreview.IsAnalysing => _analysing;
        double IMediaPreview.AnalysisProgress => Progress;
        string? IMediaPreview.AnalysisStage => _analysing ? Stage : null;

        public double Duration => 3734;
        public double FrameRate => 30;
        public IReadOnlyList<double> Keyframes => [];
        public int AudioStreamCount => 1;
        public bool IsPlaceholder => false;
        public bool IsPlayable => false;
        public string? Activity => _analysing ? "analysing 72%" : null;
        public string? AnalysisError => null;

        public event EventHandler? Changed;

        public double AudioPeak(int stream, double startTime, double endTime) => 0.5;

        public void DrawFrame(DrawingContext context, Rect rect, double time, FrameLook look, int variant) =>
            context.FillRectangle(Brushes.DimGray, rect);
    }

    private sealed class AnalysingOpener(AnalysingPreview preview) : IMediaOpener
    {
        public Task<OpenedMedia> OpenAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenedMedia(new SourceMedia(path, 3734, 30, [new AudioTrack(1, "Mic")]), preview,
                "interview-final.mp4 · 1080p · 30 fps"));
    }
}
