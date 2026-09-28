using Avalonia;
using Avalonia.Media;

namespace HighlightCut.App.Controls;

/// <summary>
/// CSS <c>repeating-linear-gradient(135deg, a 0 w, b w 2w)</c>: diagonal bands of equal width
/// <c>w</c>, measured across the bands, starting with <c>a</c> at the top-left corner.
/// </summary>
public static class Stripes
{
    public static void Draw(DrawingContext context, Rect rect, Color a, Color b, double width)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        context.FillRectangle(new SolidColorBrush(a), rect);
        // Band k of colour b covers (x + y) / √2 in [(2k + 1)·w, (2k + 2)·w).
        double step = 2 * width * Math.Sqrt(2);
        double first = 1.5 * width * Math.Sqrt(2);
        var pen = new Pen(new SolidColorBrush(b), width);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (double s = first; s - width * 2 <= rect.Width + rect.Height; s += step)
            {
                g.BeginFigure(new Point(rect.X + s + width, rect.Y - width), false);
                g.LineTo(new Point(rect.X + s - rect.Height - width, rect.Y + rect.Height + width));
                g.EndFigure(false);
            }
        }
        using (context.PushClip(rect))
            context.DrawGeometry(null, pen, geo);
    }
}
