using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Views;

/// <summary>
/// Clip list. Rows are reordered with a pointer drag inside the list (no OS drag and drop),
/// a plain click selects the clip.
/// </summary>
public partial class ClipsPanel : UserControl
{
    private const double DragThreshold = 4;
    private ClipViewModel? _pressed;
    private Point _pressPoint;
    private bool _dragging;
    private ClipViewModel? _target;

    public ClipsPanel()
    {
        InitializeComponent();
        ClipList.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        ClipList.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        ClipList.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        ClipList.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(commit: false), RoutingStrategies.Bubble);
    }

    private EditorViewModel? Editor => DataContext as EditorViewModel;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(ClipList).Properties;
        if (props.IsRightButtonPressed)
        {
            // The context menu acts on the selected clip.
            if (RowAt(e.Source as Visual) is { } row)
                Editor?.Select(row);
            return;
        }
        if (!props.IsLeftButtonPressed)
            return;
        if ((e.Source as Visual)?.FindAncestorOfType<CheckBox>(includeSelf: true) is not null
            || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        _pressed = RowAt(e.Source as Visual);
        _pressPoint = e.GetPosition(ClipList);
        _dragging = false;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null || Editor is null)
            return;
        var p = e.GetPosition(ClipList);
        if (!_dragging)
        {
            if (Math.Abs(p.Y - _pressPoint.Y) < DragThreshold && Math.Abs(p.X - _pressPoint.X) < DragThreshold)
                return;
            _dragging = true;
            _pressed.IsDragSource = true;
            e.Pointer.Capture(ClipList);
        }
        SetTarget(ClipAt(p.Y));
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
            Editor.SelectFromList(_pressed);
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
        if (commit && target is not null && Editor is { } editor)
            editor.MoveClip(editor.Clips.IndexOf(source), editor.Clips.IndexOf(target));
    }

    private void SetTarget(ClipViewModel? clip)
    {
        if (ReferenceEquals(clip, _target))
            return;
        if (_target is not null)
            _target.IsDropTarget = false;
        _target = ReferenceEquals(clip, _pressed) ? null : clip;
        if (_target is not null)
            _target.IsDropTarget = true;
    }

    /// <summary>
    /// A swatch in a clip's colour picker was clicked: the edit is made by its command, then the picker closes. Closing
    /// waits a moment, since the command runs after this handler and the swatch loses its data once the picker is gone.
    /// </summary>
    private void OnColorPicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.FindLogicalAncestorOfType<Popup>() is { PlacementTarget: Button { Flyout: { } flyout } })
            Dispatcher.UIThread.Post(flyout.Hide);
    }

    private static ClipViewModel? RowAt(Visual? visual) =>
        visual?.FindAncestorOfType<ContentPresenter>(includeSelf: true)?.DataContext as ClipViewModel;

    private ClipViewModel? ClipAt(double y)
    {
        foreach (var container in ClipList.GetRealizedContainers())
        {
            if (container.DataContext is ClipViewModel clip && y >= container.Bounds.Top && y < container.Bounds.Bottom)
                return clip;
        }
        return null;
    }
}
