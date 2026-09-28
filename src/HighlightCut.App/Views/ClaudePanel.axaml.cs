using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Views;

public partial class ClaudePanel : UserControl
{
    private ClaudePanelViewModel? _vm;
    private DispatcherTimer? _clock;

    public ClaudePanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ClaudePanelViewModel);
    }

    private void Attach(ClaudePanelViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.Log.CollectionChanged -= OnLogChanged;
            _vm.Export.PropertyChanged -= OnExportChanged;
        }
        _vm = vm;
        if (_vm is not null)
        {
            _vm.Log.CollectionChanged += OnLogChanged;
            _vm.Export.PropertyChanged += OnExportChanged;
        }
        ScrollToTop();
    }

    private void OnExportChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClaudeExportViewModel.Stage))
            ScrollToTop();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // "18 min ago" moves on by itself.
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _vm?.RefreshTimes(DateTimeOffset.Now));
        _clock.Start();
        _vm?.RefreshTimes(DateTimeOffset.Now);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _clock?.Stop();
        _clock = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLogChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _vm?.RefreshTimes(DateTimeOffset.Now);
        ScrollToTop();
    }

    /// <summary>Keeps the newest card (at the top) in view.</summary>
    private void ScrollToTop() => Dispatcher.UIThread.Post(() => LogScroller.ScrollToHome(), DispatcherPriority.Background);

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _vm.IsOpen = !_vm.IsOpen;
            ScrollToTop();
        }
    }
}
