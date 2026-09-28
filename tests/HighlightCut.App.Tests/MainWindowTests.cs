using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using HighlightCut.App.Views;

namespace HighlightCut.App.Tests;

public class MainWindowTests
{
    [AvaloniaFact]
    public void Main_window_opens_and_renders_a_frame()
    {
        var window = new MainWindow();
        window.Show();

        using var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.Equal("HighlightCut", window.Title);
        window.Close();
    }
}
