using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Views;

/// <summary>
/// The welcome tour's dialog. While it is open, focus is in it (Continue at first) and Tab stays in it; when it closes,
/// focus goes back to where it was. Enter continues and the arrows change the step, unless focus is in a control that
/// needs those keys. The note after it sits just above the player controls.
/// </summary>
public partial class WelcomeTour : UserControl
{
    /// <summary>Below this dialog width the step list narrows.</summary>
    private const double NarrowWidth = 700;

    private WelcomeTourViewModel? _tour;
    private TopLevel? _topLevel;
    private IInputElement? _focusBefore;

    public WelcomeTour()
    {
        InitializeComponent();
        Dialog.SizeChanged += (_, e) => Dialog.Classes.Set("narrow", e.NewSize.Width < NarrowWidth);
        LayoutUpdated += (_, _) => PlaceNote();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_tour is not null)
            _tour.PropertyChanged -= OnTourChanged;
        _tour = DataContext as WelcomeTourViewModel;
        if (_tour is not null)
            _tour.PropertyChanged += OnTourChanged;
        base.OnDataContextChanged(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // On the window, after its own shortcuts (Esc, the Open video key), so the keys reach the tour whatever has focus.
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        if (_tour is { IsOpen: true })
            FocusDialog();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTourChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WelcomeTourViewModel.IsOpen) when _tour!.IsOpen:
                _focusBefore = FocusedElement is { } focused && !IsInDialog(focused) ? focused : null;
                FocusDialog();
                break;
            case nameof(WelcomeTourViewModel.IsOpen):
                RestoreFocus();
                break;
            case nameof(WelcomeTourViewModel.Step):
                // Back is hidden on the first step, and a step's controls go with it: focus moves to Continue.
                StepScroll.Offset = default;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_tour is { IsOpen: true } && FocusedElement is not Visual { IsEffectivelyVisible: true })
                        NextButton.Focus();
                }, DispatcherPriority.Input);
                break;
        }
    }

    private IInputElement? FocusedElement => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

    private bool IsInDialog(IInputElement element) => element is Visual v && (ReferenceEquals(v, Dialog) || Dialog.IsVisualAncestorOf(v));

    /// <summary>Continue takes focus once the dialog is shown.</summary>
    private void FocusDialog() => Dispatcher.UIThread.Post(() =>
    {
        if (_tour is { IsOpen: true } && (FocusedElement is not { } focused || !IsInDialog(focused)))
            NextButton.Focus();
    }, DispatcherPriority.Input);

    private void RestoreFocus()
    {
        var back = _focusBefore;
        _focusBefore = null;
        if (back is InputElement { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true } element
            && TopLevel.GetTopLevel(element) is not null)
            element.Focus();
        else if (FocusedElement is { } focused && IsInDialog(focused))
            TopLevel.GetTopLevel(this)?.Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _tour is not { IsOpen: true } || e.KeyModifiers != KeyModifiers.None)
            return;
        var focused = FocusedElement;
        if (NeedsKeys(focused))
            return;
        // A focused button of the dialog (Skip, Back, a step) does its own Enter.
        if (e.Key == Key.Enter && focused is Button button && !ReferenceEquals(button, NextButton))
            return;
        e.Handled = _tour.HandleKey(e.Key);
    }

    /// <summary>Controls that use Enter or the arrows themselves: text, lists, sliders and drop-downs (open or not).</summary>
    private static bool NeedsKeys(IInputElement? element) => element is Control control
        && (control is TextBox or SelectableTextBlock or ComboBox or ListBox or Slider or NumericUpDown
            || control.FindAncestorOfType<ComboBox>() is not null || control.FindAncestorOfType<ListBox>() is not null);

    /// <summary>
    /// Puts the note after the tour a little above the player controls, wherever the timeline's height puts them; at the
    /// bottom when there is no player (a test with the tour alone).
    /// </summary>
    private void PlaceNote()
    {
        if (!ReopenNote.IsVisible)
            return;
        double bottom = 24;
        var transport = TopLevel.GetTopLevel(this)?.GetVisualDescendants().OfType<PlayerPanel>().FirstOrDefault()?.FindControl<Control>("Transport");
        if (transport?.TranslatePoint(default, this) is { } top)
            bottom = Math.Max(24, Bounds.Height - top.Y + 28);
        if (ReopenNote.Margin.Bottom != bottom)
            ReopenNote.Margin = new Thickness(0, 0, 0, bottom);
    }
}
