using Avalonia.Data.Converters;

namespace HighlightCut.App.Controls;

public static class Converters
{
    public static readonly IValueConverter IsPositive = new FuncValueConverter<double, bool>(v => v > 0.5);
}
