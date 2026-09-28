using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Views;

/// <summary>
/// The Transcript tab. A click on a word moves the playhead there; a drag across words selects them (pointer events on
/// the list, like the clip list's reordering). The list scrolls to the words the view model asks for.
/// </summary>
public partial class TranscriptPanel : UserControl
{
    private TranscriptPanelViewModel? _vm;
    private int _anchor = -1;
    private bool _moved;

    public TranscriptPanel()
    {
        InitializeComponent();
        ParagraphList.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        ParagraphList.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        ParagraphList.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        ParagraphList.AddHandler(PointerCaptureLostEvent, (_, _) => _anchor = -1, RoutingStrategies.Bubble);
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
            _vm.ScrollRequested -= OnScrollRequested;
        _vm = DataContext as TranscriptPanelViewModel;
        if (_vm is not null)
            _vm.ScrollRequested += OnScrollRequested;
        base.OnDataContextChanged(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Opening on this tab shows the word under the playhead (prototype enter()).
        _vm?.RevealCurrentWord();
    }

    // ---- Selecting words ----------------------------------------------------------------------

    private static TranscriptWordViewModel? WordOf(object? element) => (element as StyledElement)?.DataContext as TranscriptWordViewModel;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ParagraphList).Properties.IsLeftButtonPressed || WordOf(e.Source) is not { } word)
            return;
        _anchor = word.Index;
        _moved = false;
        e.Pointer.Capture(ParagraphList);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_anchor < 0 || _vm is null)
            return;
        if (WordOf(ParagraphList.InputHitTest(e.GetPosition(ParagraphList))) is not { } word)
            return;
        if (word.Index != _anchor)
            _moved = true;
        if (_moved)
            _vm.Select(_anchor, word.Index);
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_anchor < 0)
            return;
        int anchor = _anchor;
        _anchor = -1;
        if (!_moved)
            _vm?.SeekToWord(anchor);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ---- Search -------------------------------------------------------------------------------

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm is null)
            return;
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? _vm.PreviousMatchCommand : _vm.NextMatchCommand;
        if (command.CanExecute(null))
            command.Execute(null);
        e.Handled = true;
    }

    // ---- Scrolling ----------------------------------------------------------------------------

    private void OnScrollRequested(object? sender, TranscriptScrollEventArgs e)
    {
        // After layout: the list may have just become visible, or the paragraph may not be realized yet.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsEffectivelyVisible || _vm is null || _vm.ParagraphIndexOf(e.WordIndex) is not (>= 0 and var paragraph))
                return;
            ParagraphList.ScrollIntoView(paragraph);
            Dispatcher.UIThread.Post(() => ScrollToWord(paragraph, e.WordIndex, e.Center, attempts: 3), DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }

    private void ScrollToWord(int paragraph, int index, bool center, int attempts)
    {
        if (FindWord(paragraph, index) is not { } word || word.TranslatePoint(default, Scroller) is not { } point)
            return;
        double offset = Scroller.Offset.Y, viewport = Scroller.Viewport.Height;
        double top = point.Y + offset;
        if (!center && top >= offset + 16 && top <= offset + viewport - 40)
            return;
        double target = Math.Max(0, top - viewport / 3);
        if (Math.Abs(target - offset) < 1)
            return;
        Scroller.Offset = new Vector(0, target);
        // Paragraphs realized by the jump can differ from the list's estimate and move the word: look again.
        if (attempts > 0)
            Dispatcher.UIThread.Post(() => ScrollToWord(paragraph, index, center, attempts - 1), DispatcherPriority.Loaded);
    }

    private Border? FindWord(int paragraph, int index) =>
        ParagraphList.ContainerFromIndex(paragraph)?.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.DataContext is TranscriptWordViewModel w && w.Index == index);
}
