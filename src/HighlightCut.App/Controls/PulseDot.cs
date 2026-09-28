using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls.Shapes;
using Avalonia.Styling;

namespace HighlightCut.App.Controls;

/// <summary>A dot that fades between full and 30 % opacity every 1.2 s ("cl-pulse" in the design).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001", Justification = "Cancelled and disposed when detached from the visual tree.")]
public sealed class PulseDot : Ellipse
{
    private static readonly Animation Pulse = new()
    {
        Duration = TimeSpan.FromSeconds(1.2),
        IterationCount = IterationCount.Infinite,
        Easing = new SineEaseInOut(),
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1.0) } },
            new KeyFrame { Cue = new Cue(0.5), Setters = { new Setter(OpacityProperty, 0.3) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0) } },
        },
    };

    private CancellationTokenSource? _cts;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _cts = new CancellationTokenSource();
        _ = Pulse.RunAsync(this, _cts.Token);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        base.OnDetachedFromVisualTree(e);
    }
}
