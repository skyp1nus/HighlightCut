using Avalonia.Controls;
using Avalonia.Input;
using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Views;

public partial class TimelinePanel : UserControl
{
    public TimelinePanel() => InitializeComponent();

    /// <summary>Double-clicking a lane's volume puts it back to 0 dB.</summary>
    private void OnVolumeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AudioLaneViewModel lane)
        {
            lane.ResetVolumeCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>The wheel moves a lane's volume in half-decibel steps.</summary>
    private void OnVolumeWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((sender as Control)?.DataContext is AudioLaneViewModel lane && e.Delta.Y != 0)
        {
            lane.Nudge(Math.Sign(e.Delta.Y));
            e.Handled = true;
        }
    }
}
