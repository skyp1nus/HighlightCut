using Avalonia;
using Avalonia.Controls;

namespace HighlightCut.App.Controls;

/// <summary>Sizes its child to the largest rectangle with the given aspect ratio that fits, centred.</summary>
public sealed class AspectPanel : Decorator
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectPanel, double>(nameof(Ratio), 16.0 / 9.0);

    static AspectPanel() => AffectsMeasure<AspectPanel>(RatioProperty);

    public double Ratio { get => GetValue(RatioProperty); set => SetValue(RatioProperty, value); }

    private Size Fit(Size available)
    {
        double w = available.Width, h = available.Height;
        if (double.IsInfinity(w) && double.IsInfinity(h))
            return default;
        if (double.IsInfinity(w) || w > h * Ratio)
            w = h * Ratio;
        else
            h = w / Ratio;
        return new Size(w, h);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Fit(availableSize);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = Fit(finalSize);
        Child?.Arrange(new Rect((finalSize.Width - size.Width) / 2, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
        return finalSize;
    }
}
