using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace HighlightCut.App.Controls;

/// <summary>
/// Draws an icon from SVG path data in its own view box (24 by default), stroked and/or filled
/// with the inherited foreground, like the inline SVGs in the design.
/// </summary>
public sealed class SvgIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<SvgIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> ViewBoxProperty =
        AvaloniaProperty.Register<SvgIcon, double>(nameof(ViewBox), 24);

    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<SvgIcon, double>(nameof(StrokeWidth), 0);

    public static readonly StyledProperty<bool> IsFilledProperty =
        AvaloniaProperty.Register<SvgIcon, bool>(nameof(IsFilled));

    public static readonly StyledProperty<PenLineCap> LineCapProperty =
        AvaloniaProperty.Register<SvgIcon, PenLineCap>(nameof(LineCap), PenLineCap.Round);

    public static readonly StyledProperty<PenLineJoin> LineJoinProperty =
        AvaloniaProperty.Register<SvgIcon, PenLineJoin>(nameof(LineJoin), PenLineJoin.Round);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<SvgIcon>();

    static SvgIcon()
    {
        AffectsRender<SvgIcon>(DataProperty, ViewBoxProperty, StrokeWidthProperty, IsFilledProperty,
            LineCapProperty, LineJoinProperty, ForegroundProperty);
        AffectsMeasure<SvgIcon>(ViewBoxProperty);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double ViewBox { get => GetValue(ViewBoxProperty); set => SetValue(ViewBoxProperty, value); }
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public bool IsFilled { get => GetValue(IsFilledProperty); set => SetValue(IsFilledProperty, value); }
    public PenLineCap LineCap { get => GetValue(LineCapProperty); set => SetValue(LineCapProperty, value); }
    public PenLineJoin LineJoin { get => GetValue(LineJoinProperty); set => SetValue(LineJoinProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public override void Render(DrawingContext context)
    {
        var data = Data;
        var brush = Foreground;
        if (data is null || brush is null || ViewBox <= 0)
            return;

        double scale = Math.Min(Bounds.Width, Bounds.Height) / ViewBox;
        double dx = (Bounds.Width - ViewBox * scale) / 2;
        double dy = (Bounds.Height - ViewBox * scale) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
        {
            var pen = StrokeWidth > 0
                ? new Pen(brush, StrokeWidth, lineCap: LineCap, lineJoin: LineJoin)
                : null;
            context.DrawGeometry(IsFilled ? brush : null, pen, data);
        }
    }
}
