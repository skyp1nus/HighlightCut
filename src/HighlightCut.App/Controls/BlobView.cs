using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HighlightCut.App.Controls;

/// <summary>
/// The processing screen's blob (design: "HighlightCut — екран обробки", X1): a soft shape that slowly changes form and turns
/// once every <see cref="Period"/> seconds, lit from the top left (white, through <see cref="Accent"/>, to
/// <see cref="Second"/>), over two glows of those colours. Redrawn every frame while <see cref="IsPlaying"/>.
/// </summary>
public sealed class BlobView : Control
{
    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<BlobView, Color>(nameof(Accent), Color.Parse("#60CDFF"));

    public static readonly StyledProperty<Color> SecondProperty =
        AvaloniaProperty.Register<BlobView, Color>(nameof(Second), Color.Parse("#A78BFA"));

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<BlobView, bool>(nameof(IsPlaying), true);

    /// <summary>Seconds for one turn, as in the design.</summary>
    public const double Period = 8;

    /// <summary>Points around the outline: at this size the straight pieces between them do not show.</summary>
    private const int Points = 72;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IBrush? _body, _accentGlow, _secondGlow;
    private bool _attached, _frameRequested;

    static BlobView()
    {
        AffectsRender<BlobView>(AccentProperty, SecondProperty);
    }

    public Color Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public Color Second
    {
        get => GetValue(SecondProperty);
        set => SetValue(SecondProperty, value);
    }

    /// <summary>Off while the screen is hidden: no frames are asked for.</summary>
    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AccentProperty || change.Property == SecondProperty)
            _body = _accentGlow = _secondGlow = null;
        else if (change.Property == IsPlayingProperty)
            RequestFrame();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void RequestFrame()
    {
        if (!_attached || !IsPlaying || _frameRequested || TopLevel.GetTopLevel(this) is not { } top)
            return;
        _frameRequested = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            InvalidateVisual();
            RequestFrame();
        });
    }

    public override void Render(DrawingContext context)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;
        double t = _clock.Elapsed.TotalSeconds;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        // The design's 150 px box holds a 92 px blob over 110 and 100 px glows.
        double radius = size * 92 / 150 / 2;
        _body ??= new RadialGradientBrush
        {
            // The design's radial-gradient(circle at 32% 28%, …): it turns with the shape.
            Center = new RelativePoint(0.32, 0.28, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.32, 0.28, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.99, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.99, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Accent, 0.34), new GradientStop(Second, 1) },
        };
        _secondGlow ??= Glow(Second, 0.35);
        _accentGlow ??= Glow(Accent, 0.4);

        // The glows: the design's blurred copies, as soft discs that breathe with the shape (the second turns the other way).
        context.DrawEllipse(_secondGlow, null, center, radius * 1.9 * Breath(t + 2), radius * 1.9 * Breath(t + 3));
        context.DrawEllipse(_accentGlow, null, center, radius * 1.6 * Breath(t), radius * 1.6 * Breath(t + 1));

        double turn = t / Period * 360;
        using (context.PushTransform(Matrix.CreateTranslation(-center.X, -center.Y)
                   * Matrix.CreateRotation(turn * Math.PI / 180)
                   * Matrix.CreateTranslation(center.X, center.Y)))
        {
            context.DrawGeometry(_body, null, Outline(center, radius * Breath(t), t));
        }
    }

    /// <summary>The design's 1 → 1.04 → 0.97 → 1.03 over a turn, smoothed.</summary>
    private static double Breath(double t)
    {
        double phase = t / Period * 2 * Math.PI;
        return 1 + 0.025 * Math.Sin(phase) + 0.012 * Math.Sin(2 * phase + 0.8);
    }

    /// <summary>
    /// A round shape whose radius wobbles with three slow waves around it, as the design's changing corner radii: never
    /// a circle, never the same twice in a turn.
    /// </summary>
    internal static StreamGeometry Outline(Point center, double radius, double t)
    {
        double w = t / Period * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        for (int i = 0; i <= Points; i++)
        {
            double a = i * 2 * Math.PI / Points;
            double r = radius * (1
                + 0.075 * Math.Sin(2 * a + w)
                + 0.05 * Math.Sin(3 * a - 2 * w + 1.3)
                + 0.025 * Math.Sin(4 * a + 3 * w + 2.1));
            var p = new Point(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a));
            if (i == 0)
                g.BeginFigure(p, true);
            else
                g.LineTo(p);
        }
        g.EndFigure(true);
        return geometry;
    }

    private static RadialGradientBrush Glow(Color color, double opacity) => new()
    {
        GradientStops =
        {
            new GradientStop(Color.FromArgb((byte)(255 * opacity), color.R, color.G, color.B), 0),
            new GradientStop(Color.FromArgb((byte)(255 * opacity * 0.45), color.R, color.G, color.B), 0.45),
            new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
        },
    };
}
