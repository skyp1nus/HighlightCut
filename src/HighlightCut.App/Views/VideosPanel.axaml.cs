using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Views;

/// <summary>
/// The Videos list. Rows are reordered with a pointer drag inside the list, like the clip list; a plain click moves the
/// playhead to where the video starts.
/// </summary>
public partial class VideosPanel : UserControl
{
    private const double DragThreshold = 4;
    private VideoViewModel? _pressed;
    private Point _pressPoint;
    private bool _dragging;
    private VideoViewModel? _target;

    public VideosPanel()
    {
        InitializeComponent();
        VideoList.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        VideoList.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        VideoList.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        VideoList.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(commit: false), RoutingStrategies.Bubble);
    }

    private EditorViewModel? Editor => DataContext as EditorViewModel;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(VideoList).Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        _pressed = RowAt(e.Source as Visual);
        _pressPoint = e.GetPosition(VideoList);
        _dragging = false;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null || Editor is null)
            return;
        var p = e.GetPosition(VideoList);
        if (!_dragging)
        {
            if (Math.Abs(p.Y - _pressPoint.Y) < DragThreshold && Math.Abs(p.X - _pressPoint.X) < DragThreshold)
                return;
            _dragging = true;
            _pressed.IsDragSource = true;
            e.Pointer.Capture(VideoList);
        }
        SetTarget(VideoAt(p.Y));
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed is null || Editor is null)
            return;
        if (_dragging)
        {
            EndDrag(commit: true);
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        else
        {
            Editor.GoToVideo(_pressed.SourceId);
            _pressed = null;
        }
    }

    private void EndDrag(bool commit)
    {
        if (_pressed is null)
            return;
        var source = _pressed;
        var target = _target;
        _pressed = null;
        _dragging = false;
        source.IsDragSource = false;
        SetTarget(null);
        if (commit && target is not null)
            Editor?.MoveVideo(source.SourceId, target.Number - 1);
    }

    private void SetTarget(VideoViewModel? video)
    {
        if (ReferenceEquals(video, _target))
            return;
        if (_target is not null)
            _target.IsDropTarget = false;
        _target = ReferenceEquals(video, _pressed) ? null : video;
        if (_target is not null)
            _target.IsDropTarget = true;
    }

    private static VideoViewModel? RowAt(Visual? visual) =>
        visual?.FindAncestorOfType<ContentPresenter>(includeSelf: true)?.DataContext as VideoViewModel;

    private VideoViewModel? VideoAt(double y)
    {
        foreach (var container in VideoList.GetRealizedContainers())
        {
            if (container.DataContext is VideoViewModel video && y >= container.Bounds.Top && y < container.Bounds.Bottom)
                return video;
        }
        return null;
    }
}
