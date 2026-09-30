using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HighlightCut.App.Controls;

/// <summary>Fills its bounds with <see cref="Stripes"/> (an excluded clip in the welcome tour's sample timeline).</summary>
public sealed class StripeFill : Control
{
    public static readonly StyledProperty<Color> ColorAProperty = AvaloniaProperty.Register<StripeFill, Color>(nameof(ColorA));
    public static readonly StyledProperty<Color> ColorBProperty = AvaloniaProperty.Register<StripeFill, Color>(nameof(ColorB));
    public static readonly StyledProperty<double> BandProperty = AvaloniaProperty.Register<StripeFill, double>(nameof(Band), 4);

    static StripeFill() => AffectsRender<StripeFill>(ColorAProperty, ColorBProperty, BandProperty);

    public Color ColorA { get => GetValue(ColorAProperty); set => SetValue(ColorAProperty, value); }
    public Color ColorB { get => GetValue(ColorBProperty); set => SetValue(ColorBProperty, value); }
    public double Band { get => GetValue(BandProperty); set => SetValue(BandProperty, value); }

    public override void Render(DrawingContext context) => Stripes.Draw(context, new Rect(Bounds.Size), ColorA, ColorB, Band);
}
