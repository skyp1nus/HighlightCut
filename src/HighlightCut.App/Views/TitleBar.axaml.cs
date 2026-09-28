using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HighlightCut.App.Views;

public partial class TitleBar : UserControl
{
    public TitleBar() => InitializeComponent();

    private Window? HostWindow => TopLevel.GetTopLevel(this) as Window;

    private void OnMinimize(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } w)
            w.WindowState = WindowState.Minimized;
    }

    private void OnMaximize(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } w)
            w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => HostWindow?.Close();

    private void OnExit(object? sender, RoutedEventArgs e) => HostWindow?.Close();
}
