using Avalonia.Media;
using Avalonia.Media.Immutable;
using OurCut.Core.Model;

namespace OurCut.App;

/// <summary>Brushes for the clip colours of <see cref="ClipPalette"/>, made once and shared.</summary>
public static class ClipBrushes
{
    private static readonly Dictionary<(ClipColor, byte), IImmutableSolidColorBrush> Cache = [];

    public static Color ColorOf(ClipColor color) => Color.Parse(ClipPalette.Hex(color));

    /// <summary>The colour itself: swatches and the stripe on a timeline segment.</summary>
    public static IImmutableSolidColorBrush Solid(ClipColor color) => Tint(color, 1);

    /// <summary>The colour at <paramref name="opacity"/> (0–1), e.g. the fill of a timeline segment.</summary>
    public static IImmutableSolidColorBrush Tint(ClipColor color, double opacity)
    {
        byte alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        if (!Cache.TryGetValue((color, alpha), out var brush))
        {
            var c = ColorOf(color);
            Cache[(color, alpha)] = brush = new ImmutableSolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }
        return brush;
    }
}
