using Avalonia;
using Avalonia.Media;

namespace HighlightCut.App.Controls;

/// <summary>
/// CSS <c>repeating-linear-gradient(135deg, color 0 1px, transparent 1px period)</c>:
/// 1 px diagonal lines (rising to the right), starting at the top-left corner.
/// </summary>
public static class Hatch
{
    public static void Draw(DrawingContext context, Rect rect, Rect visible, IBrush brush, double period) =>
        Draw(context, rect, visible, brush, period, 1);

    /// <summary>Lines <paramref name="width"/> px wide every <paramref name="period"/> px (measured across the lines).</summary>
    public static void Draw(DrawingContext context, Rect rect, Rect visible, IBrush brush, double period, double width)
    {
        var area = rect.Intersect(visible);
        if (area.Width <= 0 || area.Height <= 0)
            return;
        // Stripe k covers points where (x + y) / √2 is in [k·period, k·period + 1).
        double step = period * Math.Sqrt(2);
        double half = width / 2 * Math.Sqrt(2);
        var pen = new Pen(brush, width);
        double x0 = area.X - rect.X, y0 = area.Y - rect.Y;
        double sMin = x0 + y0, sMax = x0 + area.Width + y0 + area.Height;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (double s = Math.Floor(sMin / step) * step + half; s <= sMax + step; s += step)
            {
                // Line x + y = s, clipped to the visible area (in rect coordinates).
                double xa = Math.Max(x0, s - (y0 + area.Height)), xb = Math.Min(x0 + area.Width, s - y0);
                if (xa >= xb)
                    continue;
                g.BeginFigure(new Point(rect.X + xa, rect.Y + s - xa), false);
                g.LineTo(new Point(rect.X + xb, rect.Y + s - xb));
                g.EndFigure(false);
            }
        }
        using (context.PushClip(area))
            context.DrawGeometry(null, pen, geo);
    }
}
