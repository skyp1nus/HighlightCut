using Avalonia.Controls;
using Avalonia.Interactivity;
using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Views.Settings;

public partial class ExportSection : UserControl
{
    // Where a token goes: the pattern box's last selection, kept while the token buttons are clicked.
    private (int Start, int End)? _patternSelection;

    public ExportSection()
    {
        InitializeComponent();
        PatternBox.PropertyChanged += (_, e) =>
        {
            if (PatternBox.IsFocused && (e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty))
                _patternSelection = (PatternBox.SelectionStart, PatternBox.SelectionEnd);
        };
    }

    private void OnTokenClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || sender is not Button { Content: string token })
            return;
        int length = vm.FileNamePattern.Length;
        var (start, end) = _patternSelection ?? (length, length);
        int caret = vm.InsertPatternToken(token, start, end);
        PatternBox.Focus();
        PatternBox.CaretIndex = caret;
        PatternBox.SelectionStart = caret;
        PatternBox.SelectionEnd = caret;
        _patternSelection = (caret, caret);
    }
}
