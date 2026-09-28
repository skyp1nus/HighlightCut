using System.ComponentModel;
using Avalonia.Controls;
using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Views;

public partial class SettingsDialog : UserControl
{
    private SettingsViewModel? _settings;

    public SettingsDialog() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_settings is not null)
            _settings.PropertyChanged -= OnSettingsChanged;
        _settings = DataContext as SettingsViewModel;
        if (_settings is not null)
            _settings.PropertyChanged += OnSettingsChanged;
    }

    // Each section starts at its top, as in the prototype.
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Section))
            Scroll.Offset = default;
    }
}
