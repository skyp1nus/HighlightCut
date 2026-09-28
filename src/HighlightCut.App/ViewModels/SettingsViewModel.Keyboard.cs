using HighlightCut.App.Services;

namespace HighlightCut.App.ViewModels;

public sealed partial class SettingsViewModel
{
    /// <summary>Settings → Keyboard.</summary>
    public KeyboardSettingsViewModel Keyboard { get; } = new();

    /// <summary>Which keys run which editor action; saved with the other settings.</summary>
    public KeyMap KeyMap => Keyboard.Map;

    /// <summary>The keys the hints show, from <see cref="KeyMap"/>.</summary>
    public ShortcutLabels Keys => _keys ??= new(KeyMap);

    private ShortcutLabels? _keys;

    partial void InitKeyboard()
    {
        KeyMap.Changed += (_, _) => UpdateSettings(s => s with { Keyboard = SavedKeyboard() });
        PropertyChanged += (_, e) =>
        {
            // Like the prototype's closeSettings and nav: the selection and the search stay.
            if (e.PropertyName is nameof(Section) or nameof(IsOpen))
                Keyboard.CancelRecording();
        };
    }

    partial void LoadKeyboard(AppSettings settings)
    {
        KeyMap.Load(settings.Keyboard);
        _settings = _settings with { Keyboard = SavedKeyboard() };
    }

    /// <summary>The design's Keyboard section: default keys, nothing selected or searched (demo mode).</summary>
    internal void LoadKeyboardDemo()
    {
        bool wasLoading = _loading;
        _loading = true;
        KeyMap.ResetAll();
        Keyboard.Clear();
        _loading = wasLoading;
    }

    // Null while every key is the default, so an untouched file keeps no keyboard section.
    private KeyboardSettings? SavedKeyboard() => KeyMap.ToSettings() is { Shortcuts: not null } changed ? changed : null;
}
