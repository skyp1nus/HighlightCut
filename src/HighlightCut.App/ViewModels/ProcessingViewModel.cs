using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HighlightCut.App.Services;

namespace HighlightCut.App.ViewModels;

/// <summary>
/// The processing screen over the editor while what the timeline chips show is read from a file being opened: its
/// keyframes and thumbnails (the "blob" design, X1). With those chips off it never shows; the waveform is read in the
/// background and never brings it up.
/// It fades in only when the reading takes longer than <see cref="ShowAfter"/>, so a file read from the cache never
/// flashes it, says what is being read and what for and about how long is left, and fades out when the editor is ready.
/// </summary>
public sealed partial class ProcessingViewModel : ViewModelBase
{
    /// <summary>Width of the progress line, as drawn.</summary>
    public const double ProgressLength = 220;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeLeftEstimator _estimator = new();
    private IMediaPreview? _media;
    private double _startedAt;
    private DispatcherTimer? _showTimer;
    private DispatcherTimer? _hideTimer;

    public ProcessingViewModel()
        : this(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(450))
    {
    }

    internal ProcessingViewModel(TimeSpan showAfter, TimeSpan fadeTime)
    {
        ShowAfter = showAfter;
        FadeTime = fadeTime;
    }

    /// <summary>A shorter analysis never shows the screen.</summary>
    public TimeSpan ShowAfter { get; }

    /// <summary>How long the fade out takes before the screen leaves the window.</summary>
    public TimeSpan FadeTime { get; }

    /// <summary>The screen is in the window (fading in, shown or fading out).</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; private set; }

    /// <summary>Faded in; false while it fades in or out.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; private set; }

    /// <summary>Fading out into the editor.</summary>
    [ObservableProperty]
    public partial bool IsLeaving { get; private set; }

    [ObservableProperty]
    public partial string FileName { get; private set; } = "";

    /// <summary>"1:02:14 · 1080p · 30 fps".</summary>
    [ObservableProperty]
    public partial string Details { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressWidth))]
    public partial double Progress { get; private set; }

    public double ProgressWidth => ProgressLength * Math.Clamp(Progress, 0, 1);

    /// <summary>"Finding keyframes (where clips can be cut without re-encoding) · 72% · about 8 s left".</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>Follows <paramref name="media"/>'s analysis; null (or a finished analysis) hides the screen.</summary>
    /// <param name="info">The file's header text, "keynote.mp4 · 4K · 29.97 fps".</param>
    public void Track(IMediaPreview? media, string fileName = "", string info = "", double duration = 0)
    {
        _media = media;
        _showTimer?.Stop();
        _showTimer = null;
        if (media is not { IsAnalysing: true })
        {
            Hide();
            return;
        }
        FileName = fileName;
        Details = DetailsFor(duration, info);
        _estimator.Reset();
        _startedAt = _clock.Elapsed.TotalSeconds;
        Refresh(media);
        if (IsVisible && !IsLeaving)
            return;
        _showTimer = new DispatcherTimer(ShowAfter, DispatcherPriority.Background, (_, _) =>
        {
            _showTimer?.Stop();
            _showTimer = null;
            if (_media is { IsAnalysing: true })
                Show();
        });
        _showTimer.Start();
    }

    /// <summary>The analysis moved on (the preview's Changed event).</summary>
    public void Update()
    {
        if (_media is not { } media)
            return;
        if (!media.IsAnalysing)
        {
            _showTimer?.Stop();
            _showTimer = null;
            Hide();
            return;
        }
        Refresh(media);
    }

    private void Refresh(IMediaPreview media)
    {
        double progress = Math.Clamp(media.AnalysisProgress, 0, 1);
        Progress = progress;
        double? left = _estimator.Add(_clock.Elapsed.TotalSeconds - _startedAt, progress);
        string percent = Math.Floor(progress * 100).ToString(CultureInfo.InvariantCulture) + "%";
        Status = $"{media.AnalysisStage ?? "Preparing"} · {percent} · {TimeLeftEstimator.Format(left)}";
    }

    private void Show()
    {
        _hideTimer?.Stop();
        _hideTimer = null;
        IsLeaving = false;
        IsVisible = true;
        // One layout pass invisible first, so the fade in starts from nothing.
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible && !IsLeaving)
                IsShown = true;
        }, DispatcherPriority.Background);
    }

    private void Hide()
    {
        if (!IsVisible || IsLeaving)
            return;
        IsLeaving = true;
        IsShown = false;
        _hideTimer = new DispatcherTimer(FadeTime, DispatcherPriority.Background, (_, _) =>
        {
            _hideTimer?.Stop();
            _hideTimer = null;
            IsVisible = false;
            IsLeaving = false;
        });
        _hideTimer.Start();
    }

    /// <summary>"1:02:14 · 4K · 29.97 fps" from the duration and the header text (whose first part is the file name).</summary>
    internal static string DetailsFor(double duration, string info)
    {
        long s = (long)Math.Round(Math.Max(0, duration));
        var c = CultureInfo.InvariantCulture;
        string length = s >= 3600
            ? string.Create(c, $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}")
            : string.Create(c, $"{s / 60}:{s % 60:00}");
        var rest = info.Split(" · ").Skip(1);
        return string.Join(" · ", rest.Prepend(length));
    }
}
