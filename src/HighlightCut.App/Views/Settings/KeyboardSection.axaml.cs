using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Views.Settings;

public partial class KeyboardSection : UserControl
{
    private KeyboardSettingsViewModel? _keyboard;

    public KeyboardSection()
    {
        InitializeComponent();
        Search.GotFocus += (_, _) => _keyboard?.CancelRecording();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_keyboard is not null)
            _keyboard.PropertyChanged -= OnKeyboardChanged;
        _keyboard = (DataContext as SettingsViewModel)?.Keyboard;
        if (_keyboard is not null)
            _keyboard.PropertyChanged += OnKeyboardChanged;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ShowActiveRow();
    }

    private void OnKeyboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KeyboardSettingsViewModel.IsRecording) or nameof(KeyboardSettingsViewModel.Conflict))
            ShowActiveRow();
    }

    private void ShowActiveRow()
    {
        if (_keyboard is { IsRecording: true } or { HasConflict: true })
            Dispatcher.UIThread.Post(FocusActiveRow, DispatcherPriority.Loaded);
    }

    // Focus leaves the search box so the recorded key cannot type into it; the conflict line may be below the fold.
    private void FocusActiveRow()
    {
        var selected = _keyboard?.Selected;
        var row = this.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("kbrow") && b.DataContext == selected);
        if (selected is null || row is null)
            return;
        row.Focus();
        row.BringIntoView();
    }
}
