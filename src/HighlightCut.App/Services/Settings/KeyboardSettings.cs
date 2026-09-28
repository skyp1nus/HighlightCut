namespace HighlightCut.App.Services;

/// <summary>Settings → Keyboard: the shortcuts that differ from the defaults, by action name ("ToggleExclude": ["Ctrl Shift E"]).</summary>
public sealed record KeyboardSettings(IReadOnlyDictionary<string, IReadOnlyList<string>>? Shortcuts = null);
