using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HighlightCut.App.Controls;

/// <summary>Fills its bounds with diagonal hatching (the "Silence" swatch).</summary>
public sealed class HatchFill : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<HatchFill, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> PeriodProperty =
        AvaloniaProperty.Register<HatchFill, double>(nameof(Period), 3);

    public static readonly StyledProperty<double> LineWidthProperty =
        AvaloniaProperty.Register<HatchFill, double>(nameof(LineWidth), 1);

    static HatchFill() => AffectsRender<HatchFill>(StrokeProperty, PeriodProperty, LineWidthProperty);

    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Period { get => GetValue(PeriodProperty); set => SetValue(PeriodProperty, value); }
    public double LineWidth { get => GetValue(LineWidthProperty); set => SetValue(LineWidthProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Stroke is { } brush)
        {
            var rect = new Rect(Bounds.Size);
            Hatch.Draw(context, rect, rect, brush, Period, LineWidth);
        }
    }
}
