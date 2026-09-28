using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering;
using Avalonia.Threading;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.Core.Model;
using HighlightCut.Media.Previews;

namespace HighlightCut.App.Controls;

/// <summary>
/// The timeline of the whole source file: ruler with scene markers, thumbnail strip with keyframe
/// ticks, the transcript lane (when shown), audio waveform with silence bands, clips as segments tinted
/// with their colour, with in/out handles, a pulsing ring on clips Claude just changed, and the playhead.
/// Layout follows design/project/HighlightCut.dc.html: ruler 22 px, video track 64 px, transcript lane 20 px,
/// audio track 72 px.
/// </summary>
public sealed class TimelineControl : Control, ICustomHitTest
{
    public static readonly StyledProperty<EditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<TimelineControl, EditorViewModel?>(nameof(Editor));

    public static readonly StyledProperty<double> ScrollOffsetProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(ScrollOffset));

    public static readonly DirectProperty<TimelineControl, double> ExtentWidthProperty =
        AvaloniaProperty.RegisterDirect<TimelineControl, double>(nameof(ExtentWidth), o => o.ExtentWidth);

    public static readonly DirectProperty<TimelineControl, double> MaxScrollProperty =
        AvaloniaProperty.RegisterDirect<TimelineControl, double>(nameof(MaxScroll), o => o.MaxScroll);

    public static readonly DirectProperty<TimelineControl, string> ZoomTextProperty =
        AvaloniaProperty.RegisterDirect<TimelineControl, string>(nameof(ZoomText), o => o.ZoomText);

    public const double RulerHeight = 22;
    public const double VideoTop = 22;
    public const double VideoHeight = 64;
    public const double LaneTop = 86;
    public const double LaneHeight = 20;
    public const double AudioHeight = 72;

    private const double SegmentInset = 2;
    private const double HandleWidth = 6;
    private const double HandleHeight = 28;
    private const double KeyframeTickHeight = 5;
    private const double SnapPixels = 8;

    /// <summary>How far the pointer moves before a press becomes a drag; less is a click, which must not trim or scrub.</summary>
    private const double DragPixels = 4;

    /// <summary>Thumbnails in the strip at 1× (the prototype's 13 frames).</summary>
    private const int FramesAtFit = 13;

    /// <summary>Waveform bars at 1× (the prototype's 440).</summary>
    private const int BarsAtFit = 440;

    /// <summary>Keyframe ticks are drawn only when they are on average at least this far apart (px).</summary>
    private const double MinKeyframeSpacing = 4;

    /// <summary>A clip Claude just changed pulses this long, then keeps a still ring.</summary>
    private static readonly TimeSpan PulseTime = TimeSpan.FromSeconds(6);

    private static readonly Color Accent = Color.Parse("#3B82F6");
    private static readonly IBrush AccentBrush = new SolidColorBrush(Accent);
    private static readonly IBrush MinorTick = White(0.12);
    private static readonly IBrush MajorTick = White(0.24);
    private static readonly IBrush RulerText = new SolidColorBrush(Color.Parse("#858687"));
    private static readonly IBrush SceneDiamond = new SolidColorBrush(Color.Parse("#9D9E9F"));
    private static readonly IBrush SceneLine = White(0.16);
    private static readonly IBrush TrackLine = White(0.05);
    private static readonly IBrush FrameGap = new SolidColorBrush(Color.Parse("#0B0C0E"));
    private static readonly IBrush PlainTrack = new SolidColorBrush(Color.Parse("#16171A"));
    private static readonly IBrush KeyframeTick = White(0.3);
    private static readonly IBrush AudioBg = new SolidColorBrush(Color.Parse("#101113"));
    private static readonly IBrush SilenceHatch = White(0.05);
    private static readonly IBrush SilenceEdge = White(0.12);
    private static readonly IBrush BarIn = White(0.55);
    private static readonly IBrush BarOut = White(0.2);
    private static readonly IBrush EmptyBg = new SolidColorBrush(Color.Parse("#131416"));
    private static readonly IBrush EmptyText = new SolidColorBrush(Color.Parse("#858687"));
    private static readonly IBrush ChipBg = new SolidColorBrush(Color.FromArgb(199, 11, 12, 14));
    private static readonly IBrush ChipNumber = new SolidColorBrush(Color.Parse("#858687"));
    // Segments are tinted with the clip's colour and carry a stripe of it along the top. Selected: a stronger tint and
    // the accent border. Excluded: no tint, a dashed grey border and a faded stripe.
    private const double SegmentTint = 0.11;
    private const double SegmentTintSelected = 0.2;
    private const double SegmentEdge = 0.4;
    private const double StripeHeight = 3;
    private const double StripeExcluded = 0.35;
    private static readonly IBrush SegmentFillExcluded = White(0.02);
    private static readonly IPen SegmentBorderSelected = new Pen(AccentBrush, 1.5);
    private static readonly IPen SegmentBorderExcluded = new Pen(White(0.25), 1, new DashStyle([3, 3], 0));
    private static readonly Dictionary<ClipColor, IPen> SegmentBorders = ClipPalette.Colors.ToDictionary(c => c,
        c => (IPen)new ImmutablePen(ClipBrushes.Tint(c, SegmentEdge), 1));
    private static readonly IBrush SnapLine = new SolidColorBrush(Color.Parse("#F2F2F2"));
    private static readonly IBrush SnapGlow = White(0.16);
    private static readonly IBrush Handle = White(0.4);
    private static readonly IBrush HandleSelected = new SolidColorBrush(Color.Parse("#F2F2F2"));
    private static readonly IBrush LaneBg = new SolidColorBrush(Color.Parse("#0F1012"));
    private static readonly IBrush ChunkFill = White(0.045);
    private static readonly IBrush ChunkEdge = White(0.2);
    private static readonly IBrush LaneWordInk = new SolidColorBrush(Color.Parse("#CECECF"));
    private static readonly IBrush LaneFillerInk = new SolidColorBrush(Color.Parse("#9D9E9F"));
    private static readonly IBrush LaneOutInk = new SolidColorBrush(Color.Parse("#71717A"));
    private static readonly IBrush LaneOutStrike = new SolidColorBrush(Color.Parse("#E671717A"));
    private static readonly IBrush LaneFillerDot = White(0.45);
    private static readonly IBrush LaneCurrent = White(0.16);
    private static readonly IBrush LaneSelected = new SolidColorBrush(Color.FromArgb(77, 59, 130, 246));
    private static readonly IBrush LanePending = White(0.035);

    private readonly List<HitRegion> _hits = [];
    private readonly List<(int Word, Rect Rect)> _laneHits = [];
    private readonly Dictionary<(int Word, IBrush Ink), FormattedText> _laneText = [];
    private readonly Dictionary<int, DateTime> _recentSince = [];
    private EditorViewModel? _editor;
    private double _extentWidth;
    private double _maxScroll;
    private string _zoomText = "1.0×";
    private Drag _drag;
    private DispatcherTimer? _pulseTimer;
    private DateTime _pulseStart = DateTime.UtcNow;
    private int _laneVersion = -1;
    private bool _placed;

    static TimelineControl()
    {
        AffectsRender<TimelineControl>(ScrollOffsetProperty);
        ClipToBoundsProperty.OverrideDefaultValue<TimelineControl>(true);
        FocusableProperty.OverrideDefaultValue<TimelineControl>(false);
    }

    public EditorViewModel? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    public double ScrollOffset { get => GetValue(ScrollOffsetProperty); set => SetValue(ScrollOffsetProperty, value); }
    public double ExtentWidth { get => _extentWidth; private set => SetAndRaise(ExtentWidthProperty, ref _extentWidth, value); }
    public double MaxScroll { get => _maxScroll; private set => SetAndRaise(MaxScrollProperty, ref _maxScroll, value); }

    /// <summary>The zoom factor for the toolbar, e.g. "1.0×".</summary>
    public string ZoomText { get => _zoomText; private set => SetAndRaise(ZoomTextProperty, ref _zoomText, value); }

    /// <summary>Largest zoom factor: one frame becomes 12 px wide.</summary>
    public double MaxZoom
    {
        get
        {
            var e = _editor;
            if (e is null || e.Duration <= 0 || Bounds.Width <= 0)
                return 8;
            return Math.Max(8, e.Duration * e.FrameRate * 12 / Bounds.Width);
        }
    }

    /// <summary>Zoom factor from the slider position: 1× (whole file) up to frame level, exponential.</summary>
    public double Zoom => Math.Pow(MaxZoom, Math.Clamp(_editor?.ZoomLevel ?? 0, 0, 1));

    private double Lane => _editor is { IsTranscriptLaneVisible: true } ? LaneHeight : 0;
    private double AudioTop => LaneTop + Lane;
    private double TotalHeight => 158 + Lane;
    private double Duration => _editor?.Duration ?? 0;
    private double Inner => Math.Max(Bounds.Width, Bounds.Width * Zoom);
    private double Pps => Duration > 0 ? Inner / Duration : 0;

    /// <summary>The scroll offset as drawn: input maps the pointer through the same value.</summary>
    private double Scroll => Math.Clamp(ScrollOffset, 0, MaxScroll);
    private double X(double t) => Duration > 0 ? t / Duration * Inner : 0;
    private double T(double x) => Inner > 0 ? x / Inner * Duration : 0;

    private static SolidColorBrush White(double alpha) => new(Color.FromArgb((byte)Math.Round(alpha * 255), 255, 255, 255));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditorProperty)
        {
            if (_editor is not null)
            {
                _editor.TimelineChanged -= OnEditorChanged;
                _editor.PropertyChanged -= OnEditorPropertyChanged;
            }
            _editor = change.GetNewValue<EditorViewModel?>();
            if (_editor is not null)
            {
                _editor.TimelineChanged += OnEditorChanged;
                _editor.PropertyChanged += OnEditorPropertyChanged;
            }
            UpdateExtent();
            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty)
        {
            UpdateExtent();
            // A zoom set before the first layout (demo screens) starts with the playhead in the middle.
            if (!_placed && Bounds.Width > 0)
            {
                _placed = true;
                double x = X(_editor?.Time ?? 0);
                if (_editor is { HasFile: true } && (x < ScrollOffset || x > ScrollOffset + Bounds.Width))
                    ScrollOffset = Math.Clamp(x - Bounds.Width / 2, 0, MaxScroll);
            }
        }
    }

    private double _lastZoom = 1;

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.ZoomLevel))
        {
            // Keep the playhead where it is on screen while zooming.
            double oldInner = Math.Max(Bounds.Width, Bounds.Width * _lastZoom);
            double anchor = Duration > 0 ? _editor!.Time / Duration * oldInner - ScrollOffset : 0;
            UpdateExtent();
            ScrollOffset = Math.Clamp(X(_editor!.Time) - anchor, 0, MaxScroll);
        }
        else if (e.PropertyName is nameof(EditorViewModel.Media) or nameof(EditorViewModel.HasFile)
                 or nameof(EditorViewModel.PlaceholderDuration) or nameof(EditorViewModel.IsTranscriptLaneVisible))
        {
            UpdateExtent();
            InvalidateVisual();
        }
        else if (e.PropertyName == nameof(EditorViewModel.Time) && _editor is { IsPlaying: true } && _drag.Kind == DragKind.None)
        {
            FollowPlayhead();
        }
        else if (e.PropertyName == nameof(EditorViewModel.Tool))
        {
            Cursor = Cursor.Default;
        }
    }

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        UpdatePulse();
        InvalidateVisual();
    }

    private void UpdateExtent()
    {
        _lastZoom = Zoom;
        ExtentWidth = Inner;
        MaxScroll = Math.Max(0, Inner - Bounds.Width);
        ZoomText = Zoom.ToString(Zoom < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + "×";
        if (ScrollOffset > MaxScroll)
            ScrollOffset = MaxScroll;
        InvalidateVisual();
    }

    private void FollowPlayhead()
    {
        double x = X(_editor!.Time);
        if (x < ScrollOffset || x > ScrollOffset + Bounds.Width - 40)
            ScrollOffset = Math.Clamp(x - Bounds.Width * 0.1, 0, MaxScroll);
    }

    /// <summary>Runs the ring animation while a clip is being edited by Claude or was just changed.</summary>
    private void UpdatePulse()
    {
        var now = DateTime.UtcNow;
        var recent = _editor?.Clips.Where(c => c.IsAiRecent).Select(c => c.Id).ToHashSet() ?? [];
        foreach (int id in _recentSince.Keys.Where(k => !recent.Contains(k)).ToList())
            _recentSince.Remove(id);
        foreach (int id in recent)
            _recentSince.TryAdd(id, now);

        bool animate = (_editor?.Clips.Any(c => c.IsAiWorking) ?? false) || _recentSince.Values.Any(t => now - t < PulseTime);
        if (animate && _pulseTimer is null)
        {
            _pulseStart = now;
            _pulseTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
            {
                InvalidateVisual();
                UpdatePulse();
            });
            _pulseTimer.Start();
        }
        else if (!animate && _pulseTimer is not null)
        {
            _pulseTimer.Stop();
            _pulseTimer = null;
            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pulseTimer?.Stop();
        _pulseTimer = null;
        base.OnDetachedFromVisualTree(e);
    }

    // ---- Rendering -----------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        _hits.Clear();
        var editor = _editor;
        if (editor is null || Bounds.Width <= 0)
            return;
        UpdatePulse();

        double scroll = Scroll;
        var visible = new Rect(scroll, 0, Bounds.Width, Bounds.Height);
        using var _ = context.PushTransform(Matrix.CreateTranslation(-scroll, 0));

        if (!editor.HasFile)
        {
            DrawEmpty(context, visible);
            return;
        }

        var media = editor.Media!;
        DrawRuler(context, editor, media, visible);
        DrawVideoTrack(context, editor, media, visible);
        DrawAudioTrack(context, editor, media, visible);
        var clips = editor.Clips.OrderBy(c => c.IsSelected).ToList();
        foreach (var clip in clips)
            DrawSegment(context, editor, clip, visible);
        _laneHits.Clear();
        if (editor.IsTranscriptLaneVisible)
            DrawTranscriptLane(context, editor.TranscriptPanel, visible);
        foreach (var clip in clips)
            DrawHandles(context, clip, visible);
        DrawRings(context, editor, visible);
        DrawJoin(context, editor);
        DrawPlayhead(context, editor);
    }

    private void DrawEmpty(DrawingContext ctx, Rect visible)
    {
        var rect = new Rect(visible.Left, VideoTop, Bounds.Width, TotalHeight - VideoTop);
        ctx.FillRectangle(EmptyBg, rect);
        var text = Text("Thumbnails, waveform and segments appear here after you open a video", SansFace(FontWeight.Normal), 12, EmptyText);
        ctx.DrawText(text, new Point(Math.Round(rect.Center.X - text.Width / 2), Math.Round(rect.Center.Y - text.Height / 2)));
    }

    private static readonly double[] RulerSteps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];

    private void DrawRuler(DrawingContext ctx, EditorViewModel editor, IMediaPreview media, Rect visible)
    {
        if (Duration <= 0)
            return;
        double pps = Pps;
        // Labelled ticks at least 84 px apart, four unlabelled ones between them.
        double step = RulerSteps.FirstOrDefault(c => c * pps >= 84);
        if (step == 0)
            step = RulerSteps[^1];

        double minor = step / 5;
        int firstMinor = (int)Math.Max(0, Math.Floor(T(visible.Left) / minor));
        for (int k = firstMinor; ; k++)
        {
            double t = k * minor;
            double x = X(t);
            if (t > Duration + 1e-9 || x > visible.Right + 1)
                break;
            if (k % 5 == 0)
                continue;
            ctx.FillRectangle(MinorTick, new Rect(Math.Floor(x), RulerHeight - 4, 1, 4));
        }

        int firstMajor = (int)Math.Max(0, Math.Floor(T(visible.Left - 80) / step));
        for (int k = firstMajor; ; k++)
        {
            double t = k * step;
            double x = X(t);
            if (t > Duration + 1e-9 || x > visible.Right + 1)
                break;
            ctx.FillRectangle(MajorTick, new Rect(Math.Floor(x), RulerHeight - 8, 1, 8));
            var label = RulerLabelText(RulerLabel(t, step));
            ctx.DrawText(label, new Point(Math.Floor(x) + 4, 3 + (12 - label.Height) / 2 + 1));
        }

        if (editor.ShowScenes)
        {
            foreach (double t in media.SceneChanges)
            {
                double x = X(t);
                if (x < visible.Left - 5 || x > visible.Right + 5)
                    continue;
                // A 5 px square turned 45°, centred on the scene change.
                var c = new Point(x, 12 + 2.5);
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    double r = 2.5 * Math.Sqrt(2);
                    g.BeginFigure(new Point(c.X, c.Y - r), true);
                    g.LineTo(new Point(c.X + r, c.Y));
                    g.LineTo(new Point(c.X, c.Y + r));
                    g.LineTo(new Point(c.X - r, c.Y));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(SceneDiamond, null, geo);
            }
        }
    }

    /// <summary>Ruler labels come back frame after frame (every frame while playing), so each is laid out once.</summary>
    private readonly Dictionary<string, FormattedText> _rulerLabels = [];

    private FormattedText RulerLabelText(string label)
    {
        if (_rulerLabels.TryGetValue(label, out var text))
            return text;
        if (_rulerLabels.Count > 500)
            _rulerLabels.Clear();
        return _rulerLabels[label] = Text(label, MonoFace(FontWeight.Normal), 10, RulerText);
    }

    private static string RulerLabel(double t, double step)
    {
        if (step < 1)
        {
            long tenths = (long)Math.Round(t * 10);
            long s = tenths / 10;
            return string.Create(CultureInfo.InvariantCulture, $"{s / 60:00}:{s % 60:00}.{tenths % 10}");
        }
        long whole = (long)Math.Round(t);
        return whole >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{whole / 60:00}:{whole % 60:00}");
    }

    private void DrawVideoTrack(DrawingContext ctx, EditorViewModel editor, IMediaPreview media, Rect visible)
    {
        double w = Inner;
        var track = new Rect(0, VideoTop, w, VideoHeight);
        using (ctx.PushClip(track))
        {
            // Frames chip off: a plain strip of the same height, with everything else on it as before.
            if (!editor.ShowFrames)
                ctx.FillRectangle(PlainTrack, new Rect(visible.Left, VideoTop, visible.Width, VideoHeight));
            else
                DrawThumbnails(ctx, media, visible, w);

            if (editor.ShowKeyframes)
                DrawKeyframes(ctx, media, visible);
            if (editor.ShowScenes)
            {
                foreach (double t in media.SceneChanges)
                    ctx.FillRectangle(SceneLine, new Rect(Math.Floor(X(t)), VideoTop, 1, VideoHeight));
            }
        }
        ctx.FillRectangle(TrackLine, new Rect(visible.Left, VideoTop, visible.Width, 1));
    }

    /// <summary>A row of thumbnails across the video track, more of them as it zooms in.</summary>
    private void DrawThumbnails(DrawingContext ctx, IMediaPreview media, Rect visible, double w)
    {
        int n = Math.Max(1, (int)Math.Round(FramesAtFit * Zoom));
        double fw = w / n;
        for (int i = (int)Math.Max(0, Math.Floor(visible.Left / fw)); i < n && i * fw < visible.Right; i++)
        {
            var r = new Rect(i * fw, VideoTop, fw, VideoHeight);
            media.DrawFrame(ctx, r, (i + 0.5) / n * Duration, FrameLook.Thumbnail, i);
            ctx.FillRectangle(FrameGap, new Rect(Math.Round(r.Right) - 1, r.Y, 1, r.Height));
        }
    }

    /// <summary>Hairline ticks along the top of the video track where lossless cuts can start.</summary>
    private void DrawKeyframes(DrawingContext ctx, IMediaPreview media, Rect visible)
    {
        var keyframes = media.Keyframes;
        if (keyframes.Count == 0 || Pps <= 0)
            return;
        int first = LowerBound(keyframes, T(visible.Left));
        int last = LowerBound(keyframes, T(visible.Right) + 1e-9);
        if (last - first > visible.Width / MinKeyframeSpacing)
            return;
        for (int i = first; i < last; i++)
            ctx.FillRectangle(KeyframeTick, new Rect(Math.Floor(X(keyframes[i])), VideoTop, 1, KeyframeTickHeight));
    }

    private static int LowerBound(IReadOnlyList<double> sorted, double value)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private void DrawAudioTrack(DrawingContext ctx, EditorViewModel editor, IMediaPreview media, Rect visible)
    {
        double w = Inner;
        ctx.FillRectangle(AudioBg, new Rect(visible.Left, AudioTop, visible.Width, AudioHeight));
        ctx.FillRectangle(TrackLine, new Rect(visible.Left, AudioTop, visible.Width, 1));

        if (editor.ShowSilences)
        {
            foreach (var s in media.Silences)
            {
                double x0 = X(s.Start), x1 = X(s.End);
                if (x1 < visible.Left || x0 > visible.Right)
                    continue;
                var band = new Rect(x0, AudioTop, x1 - x0, AudioHeight);
                Hatch.Draw(ctx, band, visible, SilenceHatch, 6, 2);
                ctx.FillRectangle(SilenceEdge, new Rect(Math.Floor(x0), AudioTop, 1, AudioHeight));
                ctx.FillRectangle(SilenceEdge, new Rect(Math.Ceiling(x1) - 1, AudioTop, 1, AudioHeight));
            }
        }

        // One lane per audio stream, sharing the track; bars are centred with a 1 px gap.
        int lanes = Math.Max(1, editor.AudioLanes.Count);
        double laneHeight = (AudioHeight - 12) / lanes;
        int count = Math.Max(1, (int)Math.Round(BarsAtFit * Zoom));
        double pitch = (w + 1) / count;
        double bw = pitch - 1;
        if (bw <= 0)
            return;
        var included = editor.Clips.Where(c => c.IsIncluded).Select(c => (c.Start, c.End)).ToList();
        int first = (int)Math.Max(0, Math.Floor(visible.Left / pitch) - 1);
        for (int lane = 0; lane < lanes; lane++)
        {
            if (lane >= media.AudioStreamCount)
                break;
            double top = AudioTop + 6 + lane * laneHeight;
            using var fade = ctx.PushOpacity(lane < editor.AudioLanes.Count && editor.AudioLanes[lane].IsMuted ? 0.3 : 1);
            // Drawn as it will sound: the lane's volume moves the bars up or down the dB scale.
            double gainDb = lane < editor.AudioLanes.Count ? editor.AudioLanes[lane].GainDb : 0;
            // Hundreds of bars a lane: one shape for those in a clip and one for the rest, rather than a draw call each.
            var barsIn = new StreamGeometry();
            var barsOut = new StreamGeometry();
            using (var gIn = barsIn.Open())
            using (var gOut = barsOut.Open())
            {
                for (int k = first; k < count; k++)
                {
                    double x = k * pitch;
                    if (x > visible.Right)
                        break;
                    double t0 = k * Duration / count, t1 = (k + 1) * Duration / count;
                    double level = WaveformData.WithGain(media.AudioPeak(lane, t0, t1), gainDb);
                    double h = Math.Round(level * 1000) / 1000 * laneHeight;
                    if (h <= 0)
                        continue;
                    double mid = (t0 + t1) / 2;
                    bool inClip = included.Any(c => mid >= c.Start && mid <= c.End);
                    AddRectangle(inClip ? gIn : gOut, new Rect(x, top + (laneHeight - h) / 2, bw, h));
                }
            }
            ctx.DrawGeometry(BarIn, null, barsIn);
            ctx.DrawGeometry(BarOut, null, barsOut);
        }
    }

    private static void AddRectangle(StreamGeometryContext g, Rect r)
    {
        g.BeginFigure(r.TopLeft, true);
        g.LineTo(r.TopRight);
        g.LineTo(r.BottomRight);
        g.LineTo(r.BottomLeft);
        g.EndFigure(true);
    }

    private Rect SegmentRect(ClipViewModel clip)
    {
        double x = X(clip.Start);
        return new Rect(x, VideoTop + SegmentInset, Math.Max(0, X(clip.End) - x), TotalHeight - VideoTop - 2 * SegmentInset);
    }

    private void DrawSegment(DrawingContext ctx, EditorViewModel editor, ClipViewModel clip, Rect visible)
    {
        var rect = SegmentRect(clip);
        if (rect.X > visible.Right + 10 || rect.Right < visible.Left - 10)
            return;
        bool sel = clip.IsSelected;
        var fill = !clip.IsIncluded ? SegmentFillExcluded : ClipBrushes.Tint(clip.Color, sel ? SegmentTintSelected : SegmentTint);
        var pen = sel ? SegmentBorderSelected : clip.IsIncluded ? SegmentBorders[clip.Color] : SegmentBorderExcluded;
        var shape = new RoundedRect(rect.Deflate(0.5), 3.5);
        ctx.DrawRectangle(fill, null, shape);
        using (ctx.PushClip(shape))
        {
            ctx.FillRectangle(ClipBrushes.Tint(clip.Color, clip.IsIncluded ? 1 : StripeExcluded),
                new Rect(rect.X, rect.Y, rect.Width, StripeHeight));
        }
        ctx.DrawRectangle(null, pen, shape);
        _hits.Add(new HitRegion(HitKind.Segment, clip) { Rect = rect });

        // Label chip: number and name.
        double maxWidth = rect.Width - 8;
        if (maxWidth > 14)
        {
            var num = Text(clip.Number.ToString(CultureInfo.InvariantCulture), MonoFace(FontWeight.Normal), 10, ChipNumber);
            var labelBrush = clip.IsIncluded ? Brushes.White : ChipNumber;
            double labelMax = Math.Max(1, maxWidth - 10 - num.Width - 5);
            var label = Text(clip.Label, SansFace(FontWeight.Normal), 10, labelBrush, labelMax);
            double chipWidth = Math.Min(maxWidth, 5 + num.Width + 5 + label.WidthIncludingTrailingWhitespace + 5);
            var chip = new Rect(rect.X + 4, rect.Y + StripeHeight + 3, chipWidth, 16);
            using (ctx.PushClip(new RoundedRect(chip, 3)))
            {
                ctx.FillRectangle(ChipBg, chip);
                ctx.DrawText(num, new Point(chip.X + 5, chip.Y + (16 - num.Height) / 2));
                ctx.DrawText(label, new Point(chip.X + 5 + num.Width + 5, chip.Y + (16 - label.Height) / 2));
            }
        }
    }

    /// <summary>In and out handles, straddling the segment's edges (over the transcript lane).</summary>
    private void DrawHandles(DrawingContext ctx, ClipViewModel clip, Rect visible)
    {
        var rect = SegmentRect(clip);
        if (rect.X > visible.Right + 10 || rect.Right < visible.Left - 10)
            return;
        var handleBrush = clip.IsSelected ? HandleSelected : Handle;
        double hy = rect.Center.Y - HandleHeight / 2;
        var left = new Rect(rect.X - HandleWidth / 2, hy, HandleWidth, HandleHeight);
        var right = new Rect(rect.Right - HandleWidth / 2, hy, HandleWidth, HandleHeight);
        ctx.DrawRectangle(handleBrush, null, new RoundedRect(left, 2));
        ctx.DrawRectangle(handleBrush, null, new RoundedRect(right, 2));
        _hits.Add(new HitRegion(HitKind.TrimIn, clip) { Rect = left.Inflate(new Thickness(2, 4)) });
        _hits.Add(new HitRegion(HitKind.TrimOut, clip) { Rect = right.Inflate(new Thickness(2, 4)) });
    }

    /// <summary>
    /// The transcript lane: the words in short chunks, coloured like the Transcript tab (current, cut out, filler,
    /// selected), a hatch where transcription has not got to yet, or a note while there is no transcript.
    /// </summary>
    private void DrawTranscriptLane(DrawingContext ctx, TranscriptPanelViewModel panel, Rect visible)
    {
        ctx.FillRectangle(LaneBg, new Rect(visible.Left, LaneTop, visible.Width, LaneHeight));
        ctx.FillRectangle(TrackLine, new Rect(visible.Left, LaneTop, visible.Width, 1));
        if (!panel.HasText)
        {
            if (panel.LaneMessage is { } message)
                DrawLaneLabel(ctx, message, visible.Left + 10);
            return;
        }
        if (_laneVersion != panel.LayoutVersion || _laneText.Count > 4000)
        {
            _laneText.Clear();
            _laneVersion = panel.LayoutVersion;
        }
        var chunks = panel.LaneChunks;
        var words = panel.Words;
        double from = T(visible.Left);
        int lo = 0, hi = chunks.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (chunks[mid].End < from)
                lo = mid + 1;
            else
                hi = mid;
        }
        for (int c = lo; c < chunks.Count; c++)
        {
            var chunk = chunks[c];
            double x = X(chunk.Start);
            if (x > visible.Right)
                break;
            var box = new Rect(x, LaneTop + 3, Math.Max(1, X(chunk.End) - x), 14);
            var shape = new RoundedRect(box, 3);
            using var clip = ctx.PushClip(shape);
            ctx.FillRectangle(ChunkFill, box);
            ctx.FillRectangle(ChunkEdge, new Rect(box.X, box.Y, 1, box.Height));
            if (box.Width < 8)
                continue;
            double wx = box.X + 3;
            for (int i = chunk.FirstWord; i < chunk.FirstWord + chunk.WordCount && i < words.Count && wx < box.Right; i++)
            {
                var word = words[i];
                var ink = word.IsCurrent ? Brushes.White : word.IsOut ? LaneOutInk : word.IsFiller ? LaneFillerInk : LaneWordInk;
                if (!_laneText.TryGetValue((i, ink), out var text))
                    _laneText[(i, ink)] = text = Text(word.Text, SansFace(FontWeight.Normal), 10, ink);
                double width = text.WidthIncludingTrailingWhitespace;
                var wordBox = new Rect(wx, box.Y, width, box.Height);
                if ((word.IsSelected ? LaneSelected : word.IsCurrent ? LaneCurrent : null) is { } highlight)
                    ctx.DrawRectangle(highlight, null, new RoundedRect(wordBox, 2));
                double top = box.Y + (box.Height - text.Height) / 2;
                ctx.DrawText(text, new Point(wx, top));
                if (word.IsOut)
                {
                    ctx.FillRectangle(LaneOutStrike, new Rect(wx, Math.Round(box.Center.Y), width, 1));
                }
                else if (word.IsFiller)
                {
                    double y = Math.Round(top + text.Baseline + 2);
                    for (double dx = 0; dx < width; dx += 2)
                        ctx.FillRectangle(LaneFillerDot, new Rect(wx + dx, y, 1, 1));
                }
                _laneHits.Add((i, wordBox.Intersect(box)));
                wx += width + 3;
            }
        }
        if (panel.IsRunning)
        {
            double x = X(panel.TranscribedUntil);
            var pending = new Rect(x, LaneTop, Math.Max(0, Inner - x), LaneHeight);
            Hatch.Draw(ctx, pending, visible, LanePending, 6, 2);
            if (x < visible.Right)
                DrawLaneLabel(ctx, "transcribing…", Math.Max(x, visible.Left) + 6);
        }
    }

    private static void DrawLaneLabel(DrawingContext ctx, string label, double x)
    {
        var text = Text(label, SansFace(FontWeight.Normal), 10, EmptyText);
        ctx.DrawText(text, new Point(x, LaneTop + (LaneHeight - text.Height) / 2));
    }

    /// <summary>The lane word at a point of the content, or -1.</summary>
    private int LaneWordAt(Point content)
    {
        foreach (var (word, rect) in _laneHits)
        {
            if (rect.Contains(content))
                return word;
        }
        return -1;
    }

    private bool InLane(Point p) => _editor is { IsTranscriptLaneVisible: true } && p.Y >= LaneTop && p.Y < LaneTop + LaneHeight;

    /// <summary>Thin blue rings on clips Claude is editing or just changed ("ocPulse" in the design).</summary>
    private void DrawRings(DrawingContext ctx, EditorViewModel editor, Rect visible)
    {
        var now = DateTime.UtcNow;
        double phase = (now - _pulseStart).TotalSeconds % 1.6 / 1.6;
        foreach (var clip in editor.Clips)
        {
            bool recent = _recentSince.TryGetValue(clip.Id, out var since);
            if (!clip.IsAiWorking && !recent)
                continue;
            double x = X(clip.Start);
            var rect = new Rect(x, VideoTop, Math.Max(0, X(clip.End) - x), TotalHeight - VideoTop);
            if (rect.X > visible.Right || rect.Right < visible.Left)
                continue;
            bool pulsing = clip.IsAiWorking || now - since < PulseTime;
            double opacity = 1, spread = 0, glow = 0;
            if (pulsing)
            {
                // box-shadow 0 → 6 px fading out by 70 %, opacity 1 → .5 → 1.
                double p = phase / 0.7;
                spread = phase < 0.7 ? 6 * p : 0;
                glow = phase < 0.7 ? 0.55 * (1 - p) : 0;
                opacity = phase < 0.7 ? 1 - 0.5 * p : 0.5 + 0.5 * (phase - 0.7) / 0.3;
            }
            if (spread > 0.1)
            {
                var glowPen = new Pen(new SolidColorBrush(Accent, glow), spread);
                ctx.DrawRectangle(null, glowPen, new RoundedRect(rect.Inflate(spread / 2), 4 + spread / 2));
            }
            using (ctx.PushOpacity(opacity))
                ctx.DrawRectangle(null, new Pen(AccentBrush, 1), new RoundedRect(rect.Deflate(0.5), 3.5));
        }
    }

    /// <summary>
    /// While a trim handle is being dragged and its end touches another clip (the magnet caught it), a line where the
    /// two meet shows they are snapped together.
    /// </summary>
    private void DrawJoin(DrawingContext ctx, EditorViewModel editor)
    {
        if (_drag is not { Kind: DragKind.TrimIn or DragKind.TrimOut, Moved: true, Clip: { } clip })
            return;
        bool inPoint = _drag.Kind == DragKind.TrimIn;
        double edge = inPoint ? clip.Start : clip.End;
        bool touches = editor.Clips.Any(c => !ReferenceEquals(c, clip) && Math.Abs((inPoint ? c.End : c.Start) - edge) < 1e-6);
        if (!touches)
            return;
        double x = X(edge);
        ctx.FillRectangle(SnapGlow, new Rect(x - 2, VideoTop, 4, TotalHeight - VideoTop));
        ctx.FillRectangle(SnapLine, new Rect(x - 0.5, VideoTop, 1, TotalHeight - VideoTop));
    }

    private void DrawPlayhead(DrawingContext ctx, EditorViewModel editor)
    {
        double x = X(editor.Time);
        ctx.FillRectangle(AccentBrush, new Rect(x - 0.5, 0, 1, TotalHeight));
        ctx.DrawRectangle(AccentBrush, null, new RoundedRect(new Rect(x - 5, 0, 10, 11), 2, 2, 5, 5));
    }

    // ---- Text helpers --------------------------------------------------------------------

    private static Typeface SansFace(FontWeight weight) => new(new FontFamily(FontSetup.Sans), FontStyle.Normal, weight);
    private static Typeface MonoFace(FontWeight weight) => new(new FontFamily(FontSetup.Mono), FontStyle.Normal, weight);

    private static FormattedText Text(string s, Typeface face, double size, IBrush brush, double maxWidth = double.PositiveInfinity)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush);
        if (!double.IsPositiveInfinity(maxWidth))
        {
            ft.MaxTextWidth = Math.Max(1, maxWidth);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        return ft;
    }

    // ---- Input ---------------------------------------------------------------------------

    /// <summary>
    /// The whole control takes the pointer. Avalonia otherwise hits only what was drawn, and a click between the
    /// ruler's ticks or on an empty stretch of a track went to whatever lies underneath.
    /// </summary>
    bool ICustomHitTest.HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private HitRegion? HitTest(Point p)
    {
        var content = new Point(p.X + Scroll, p.Y);
        // Handles first (they overhang the segments), then segments, the selected one on top.
        return _hits.LastOrDefault(h => h.Kind is HitKind.TrimIn or HitKind.TrimOut && h.Rect.Contains(content))
               ?? _hits.LastOrDefault(h => h.Kind == HitKind.Segment && h.Rect.Contains(content));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var editor = _editor;
        if (editor is null || !editor.HasFile)
            return;
        var p = e.GetPosition(this);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            // The context menu (exclude, split, join, delete) acts on the clip that was right-clicked.
            if (HitTest(p) is { Clip: { } clicked })
                editor.Select(clicked);
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        double t = Math.Clamp(T(p.X + Scroll), 0, Duration);
        var hit = HitTest(p);
        if (InLane(p) && hit?.Kind is not (HitKind.TrimIn or HitKind.TrimOut))
        {
            // The lane never splits or trims: a word moves the playhead to it, anywhere else scrubs.
            int word = LaneWordAt(new Point(p.X + Scroll, p.Y));
            if (word >= 0)
            {
                editor.TranscriptPanel.SeekToWord(word);
            }
            else
            {
                _drag = new Drag(DragKind.Scrub, null, p.X, 0, null);
                editor.ScrubTo(t, select: true);
                e.Pointer.Capture(this);
            }
        }
        else if (editor.Tool == TimelineTool.Split && hit is { Clip: { } target } && p.Y > RulerHeight)
        {
            editor.SplitAt(target, t);
        }
        else if (hit?.Kind is HitKind.TrimIn or HitKind.TrimOut && editor.Tool == TimelineTool.Select)
        {
            // The handles overhang the clip's edge, so a click on one is still a click on the timeline: it moves
            // the playhead there and selects the handle's clip. Only a drag trims.
            _drag = new Drag(hit.Kind == HitKind.TrimIn ? DragKind.TrimIn : DragKind.TrimOut, hit.Clip,
                p.X, hit.Kind == HitKind.TrimIn ? hit.Clip!.Start : hit.Clip!.End, Guid.NewGuid().ToString("N"));
            editor.ScrubTo(t, select: false);
            editor.Select(hit.Clip);
            e.Pointer.Capture(this);
        }
        else
        {
            _drag = new Drag(DragKind.Scrub, null, p.X, 0, null);
            editor.ScrubTo(t, select: true);
            e.Pointer.Capture(this);
        }
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var editor = _editor;
        var p = e.GetPosition(this);
        if (editor is null)
            return;
        if (_drag.Kind != DragKind.None && !_drag.Moved)
        {
            // A hand that shakes while clicking leaves the playhead where it was put.
            if (Math.Abs(p.X - _drag.StartX) < DragPixels)
                return;
            _drag = _drag with { Moved = true };
        }
        switch (_drag.Kind)
        {
            case DragKind.Scrub:
                editor.ScrubTo(T(p.X + Scroll), select: false);
                return;
            case DragKind.TrimIn or DragKind.TrimOut:
                double v = _drag.Origin + (p.X - _drag.StartX) / Pps;
                // Within SnapPixels the end snaps onto the next clip's edge (the magnet) or, with the Snap chip on, a
                // keyframe. Alt drags freely.
                double snap = e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? 0 : SnapPixels / Pps;
                editor.Trim(_drag.Clip!, _drag.Kind == DragKind.TrimIn, v, snap, _drag.MergeKey);
                return;
        }

        var hit = editor.HasFile ? HitTest(p) : null;
        if (InLane(p) && hit?.Kind is not (HitKind.TrimIn or HitKind.TrimOut))
        {
            Cursor = LaneWordAt(new Point(p.X + Scroll, p.Y)) >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            return;
        }
        Cursor = editor.Tool == TimelineTool.Split && hit is not null && p.Y > RulerHeight ? new Cursor(StandardCursorType.Cross)
            : hit?.Kind is HitKind.TrimIn or HitKind.TrimOut ? new Cursor(StandardCursorType.SizeWestEast)
            : hit is not null ? new Cursor(StandardCursorType.Hand)
            : Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag.Kind != DragKind.None)
        {
            _drag = default;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = default;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var editor = _editor;
        if (editor is null || !editor.HasFile)
            return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Zoom around the pointer.
            double px = e.GetPosition(this).X;
            double t = T(px + ScrollOffset);
            editor.ZoomLevel = Math.Clamp(editor.ZoomLevel + Math.Sign(e.Delta.Y) * 0.05, 0, 1);
            ScrollOffset = Math.Clamp(X(t) - px, 0, MaxScroll);
        }
        else
        {
            double delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
            ScrollOffset = Math.Clamp(ScrollOffset - delta * 60, 0, MaxScroll);
        }
        e.Handled = true;
    }

    private enum DragKind
    {
        None,
        Scrub,
        TrimIn,
        TrimOut,
    }

    /// <param name="MergeKey">Groups all trim steps of one drag into a single undo step.</param>
    /// <param name="Moved">The pointer went <see cref="DragPixels"/> from the press: a drag, no longer a click.</param>
    private readonly record struct Drag(DragKind Kind, ClipViewModel? Clip, double StartX, double Origin, string? MergeKey,
        bool Moved = false);

    private enum HitKind
    {
        Segment,
        TrimIn,
        TrimOut,
    }

    private sealed record HitRegion(HitKind Kind, ClipViewModel? Clip)
    {
        public Rect Rect { get; init; }
    }
}
