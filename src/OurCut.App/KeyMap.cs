using Avalonia.Input;
using OurCut.App.Services;

namespace OurCut.App;

/// <summary>Editor actions that have a shortcut, in the order Settings → Keyboard lists them.</summary>
public enum ShortcutAction
{
    PlayPause,
    PreviousFrame,
    NextFrame,
    JumpBack,
    JumpForward,
    SetIn,
    SetOut,
    Split,
    JoinWithNext,
    ToggleExclude,
    DeleteClip,
    SelectTool,
    Undo,
    Redo,
    OpenVideo,
    OpenProject,
    Save,
    SaveAs,
    Export,
    ZoomIn,
    ZoomOut,
    FitTimeline,
}

/// <summary>An action's group and name in Settings → Keyboard, and the keys it has out of the box.</summary>
public sealed record ShortcutInfo(ShortcutAction Action, string Group, string Name, IReadOnlyList<KeyCombo> Defaults);

/// <summary>Which keys run which action. Settings → Keyboard edits it; it is saved with the settings.</summary>
public sealed class KeyMap
{
    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers Shift = KeyModifiers.Shift;

    /// <summary>Every action, in <see cref="ShortcutAction"/> order (the design's <c>KB</c> table).</summary>
    public static IReadOnlyList<ShortcutInfo> Catalog { get; } =
    [
        Row(ShortcutAction.PlayPause, "Playback", "Play / pause", K(Key.Space)),
        Row(ShortcutAction.PreviousFrame, "Playback", "Previous frame", K(Key.Left)),
        Row(ShortcutAction.NextFrame, "Playback", "Next frame", K(Key.Right)),
        Row(ShortcutAction.JumpBack, "Playback", "Jump back", K(Key.Left, Shift)),
        Row(ShortcutAction.JumpForward, "Playback", "Jump forward", K(Key.Right, Shift)),
        Row(ShortcutAction.SetIn, "Playback", "Set in point", K(Key.I)),
        Row(ShortcutAction.SetOut, "Playback", "Set out point", K(Key.O)),
        Row(ShortcutAction.Split, "Editing", "Split at playhead", K(Key.S)),
        Row(ShortcutAction.JoinWithNext, "Editing", "Join with next clip", K(Key.J)),
        Row(ShortcutAction.ToggleExclude, "Editing", "Exclude / keep clip", K(Key.E)),
        Row(ShortcutAction.DeleteClip, "Editing", "Delete clip", K(Key.Delete), K(Key.Back)),
        Row(ShortcutAction.SelectTool, "Editing", "Select tool", K(Key.V)),
        Row(ShortcutAction.Undo, "Editing", "Undo", K(Key.Z, Ctrl)),
        Row(ShortcutAction.Redo, "Editing", "Redo", K(Key.Y, Ctrl), K(Key.Z, Ctrl | Shift)),
        Row(ShortcutAction.OpenVideo, "File", "Open video", K(Key.O, Ctrl)),
        Row(ShortcutAction.OpenProject, "File", "Open project", K(Key.O, Ctrl | Shift)),
        Row(ShortcutAction.Save, "File", "Save", K(Key.S, Ctrl)),
        Row(ShortcutAction.SaveAs, "File", "Save as", K(Key.S, Ctrl | Shift)),
        Row(ShortcutAction.Export, "File", "Export", K(Key.E, Ctrl)),
        Row(ShortcutAction.ZoomIn, "View", "Zoom in", K(Key.OemPlus, Ctrl)),
        Row(ShortcutAction.ZoomOut, "View", "Zoom out", K(Key.OemMinus, Ctrl)),
        Row(ShortcutAction.FitTimeline, "View", "Fit timeline", K(Key.D0, Ctrl)),
    ];

    private IReadOnlyList<KeyCombo>[] _keys = [.. Catalog.Select(i => i.Defaults)];

    public static ShortcutInfo Info(ShortcutAction action) => Catalog[(int)action];

    /// <summary>Raised once per change that moved a key; never when nothing changed.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<KeyCombo> this[ShortcutAction action] => _keys[(int)action];

    /// <summary>The action has exactly its default keys, in the same order.</summary>
    public bool IsDefault(ShortcutAction action) => this[action].SequenceEqual(Info(action).Defaults);

    /// <summary>The action a key runs, or null.</summary>
    public ShortcutAction? Find(KeyCombo combo) => Holder(combo, except: null);

    /// <summary>Another action that already has the key, or null.</summary>
    public ShortcutAction? FindConflict(KeyCombo combo, ShortcutAction except) => Holder(combo, except);

    /// <summary>Makes <paramref name="combo"/> the action's only key and takes it from any other action.</summary>
    public void Assign(ShortcutAction action, KeyCombo combo) => Update(keys => Give(keys, action, [combo]));

    /// <summary>Gives the action its default keys back, taking them from any action that has them now.</summary>
    public void Reset(ShortcutAction action) => Update(keys => Give(keys, action, Info(action).Defaults));

    public void ResetAll() => Update(SetDefaults);

    /// <summary>The actions whose keys differ from the defaults; an empty list means "Not set".</summary>
    public KeyboardSettings ToSettings()
    {
        var changed = Catalog.Where(i => !IsDefault(i.Action))
            .ToDictionary(i => i.Action.ToString(), i => (IReadOnlyList<string>)[.. this[i.Action].Select(k => k.ToString())], StringComparer.Ordinal);
        return new KeyboardSettings(changed.Count == 0 ? null : changed);
    }

    /// <summary>The defaults with the saved changes on top; unknown actions and keys that do not parse are skipped.</summary>
    public void Load(KeyboardSettings? settings) => Update(keys =>
    {
        SetDefaults(keys);
        foreach (var (name, texts) in settings?.Shortcuts ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            // Exact names only: Enum.TryParse would also take "3" or "Undo, Redo".
            if (texts is null || !Enum.TryParse(name, out ShortcutAction action) || action.ToString() != name)
                continue;
            var combos = new List<KeyCombo>();
            foreach (string text in texts)
            {
                if (KeyCombo.TryParse(text, out var combo) && !combos.Contains(combo))
                    combos.Add(combo);
            }
            Give(keys, action, combos);
        }
    });

    private static ShortcutInfo Row(ShortcutAction action, string group, string name, params KeyCombo[] defaults) =>
        new(action, group, name, defaults);

    private static KeyCombo K(Key key, KeyModifiers modifiers = KeyModifiers.None) => new(key, modifiers);

    private ShortcutAction? Holder(KeyCombo combo, ShortcutAction? except)
    {
        for (int i = 0; i < _keys.Length; i++)
        {
            if ((ShortcutAction)i != except && _keys[i].Contains(combo))
                return (ShortcutAction)i;
        }
        return null;
    }

    private static void SetDefaults(IReadOnlyList<KeyCombo>[] keys)
    {
        for (int i = 0; i < keys.Length; i++)
            keys[i] = Catalog[i].Defaults;
    }

    // A key runs at most one action, so Find is never ambiguous.
    private static void Give(IReadOnlyList<KeyCombo>[] keys, ShortcutAction action, IReadOnlyList<KeyCombo> combos)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            if (i != (int)action && keys[i].Any(combos.Contains))
                keys[i] = [.. keys[i].Where(k => !combos.Contains(k))];
        }
        keys[(int)action] = combos;
    }

    private void Update(Action<IReadOnlyList<KeyCombo>[]> change)
    {
        var keys = (IReadOnlyList<KeyCombo>[])_keys.Clone();
        change(keys);
        if (keys.Select((k, i) => k.SequenceEqual(_keys[i])).All(same => same))
            return;
        _keys = keys;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
