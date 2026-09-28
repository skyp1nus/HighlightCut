using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Views.Settings;

public partial class TranscriptionSection : UserControl
{
    public TranscriptionSection()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    // In a filler word "Add…" box: Enter adds the word, Backspace in the empty box removes the last one.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox { DataContext: FillerLanguageViewModel language })
            return;
        if (e.Key == Key.Enter)
        {
            language.AddDraftCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && language.RemoveLastCommand.CanExecute(null))
        {
            language.RemoveLastCommand.Execute(null);
            e.Handled = true;
        }
    }
}
