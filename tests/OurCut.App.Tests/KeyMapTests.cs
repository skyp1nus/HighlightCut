using Avalonia.Input;
using OurCut.App.Services;

namespace OurCut.App.Tests;

/// <summary>The shortcut catalog, key combos and their saved text, and editing the key map.</summary>
public class KeyMapTests
{
    private static KeyCombo K(Key key, KeyModifiers mods = KeyModifiers.None) => new(key, mods);

    private static string[] Labels(KeyMap map, ShortcutAction action) => [.. map[action].Select(k => k.Label)];

    [Fact]
    public void Catalog_lists_the_designs_groups_and_names_in_order()
    {
        var groups = KeyMap.Catalog.GroupBy(i => i.Group).Select(g => (g.Key, g.Count())).ToList();
        Assert.Equal([("Playback", 7), ("Editing", 7), ("File", 5), ("View", 3)], groups);
        Assert.Equal(
            ["Play / pause", "Previous frame", "Next frame", "Jump back", "Jump forward", "Set in point", "Set out point",
             "Split at playhead", "Join with next clip", "Exclude / keep clip", "Delete clip", "Select tool", "Undo", "Redo",
             "Open video", "Open project", "Save", "Save as", "Export",
             "Zoom in", "Zoom out", "Fit timeline"],
            KeyMap.Catalog.Select(i => i.Name));
        for (int i = 0; i < KeyMap.Catalog.Count; i++)
            Assert.Equal((ShortcutAction)i, KeyMap.Catalog[i].Action);
        Assert.Equal(Enum.GetValues<ShortcutAction>().Length, KeyMap.Catalog.Count);
    }

    [Fact]
    public void Defaults_are_the_keys_the_editor_handles_today()
    {
        var map = new KeyMap();
        var expected = new Dictionary<ShortcutAction, string[]>
        {
            [ShortcutAction.PlayPause] = ["Space"],
            [ShortcutAction.PreviousFrame] = ["←"],
            [ShortcutAction.NextFrame] = ["→"],
            [ShortcutAction.JumpBack] = ["Shift ←"],
            [ShortcutAction.JumpForward] = ["Shift →"],
            [ShortcutAction.SetIn] = ["I"],
            [ShortcutAction.SetOut] = ["O"],
            [ShortcutAction.Split] = ["S"],
            [ShortcutAction.JoinWithNext] = ["J"],
            [ShortcutAction.ToggleExclude] = ["E"],
            [ShortcutAction.DeleteClip] = ["Del", "Backspace"],
            [ShortcutAction.SelectTool] = ["V"],
            [ShortcutAction.Undo] = ["Ctrl Z"],
            [ShortcutAction.Redo] = ["Ctrl Y", "Ctrl Shift Z"],
            [ShortcutAction.OpenVideo] = ["Ctrl O"],
            [ShortcutAction.OpenProject] = ["Ctrl Shift O"],
            [ShortcutAction.Save] = ["Ctrl S"],
            [ShortcutAction.SaveAs] = ["Ctrl Shift S"],
            [ShortcutAction.Export] = ["Ctrl E"],
            [ShortcutAction.ZoomIn] = ["Ctrl ="],
            [ShortcutAction.ZoomOut] = ["Ctrl −"],
            [ShortcutAction.FitTimeline] = ["Ctrl 0"],
        };

        foreach (var action in Enum.GetValues<ShortcutAction>())
        {
            Assert.Equal(expected[action], Labels(map, action));
            Assert.True(map.IsDefault(action));
        }
    }

    [Fact]
    public void A_key_event_becomes_a_combo()
    {
        Assert.Equal("Ctrl K", KeyCombo.From(Key.K, KeyModifiers.Meta)?.Label);
        Assert.Equal("0", KeyCombo.From(Key.NumPad0, KeyModifiers.None)?.Label);
        Assert.Equal("=", KeyCombo.From(Key.Add, KeyModifiers.None)?.Label);
        Assert.Equal("Ctrl −", KeyCombo.From(Key.Subtract, KeyModifiers.Control)?.Label);
        Assert.Equal("Ctrl Alt Shift F5",
            KeyCombo.From(Key.F5, KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Control)?.Label);
        Assert.Equal("Enter", KeyCombo.From(Key.Return, KeyModifiers.None)?.Label);
        Assert.Null(KeyCombo.From(Key.LeftCtrl, KeyModifiers.Control));
        Assert.Null(KeyCombo.From(Key.RightShift, KeyModifiers.Shift));
        Assert.Null(KeyCombo.From(Key.LWin, KeyModifiers.Meta));
        Assert.Equal("Ctrl+E", K(Key.E, KeyModifiers.Control).Format('+'));
        Assert.Equal("Ctrl+Shift+Z", K(Key.Z, KeyModifiers.Control | KeyModifiers.Shift).Format('+'));
    }

    [Fact]
    public void A_combo_round_trips_through_its_saved_text()
    {
        var combos = KeyMap.Catalog.SelectMany(i => i.Defaults)
            .Concat([K(Key.Enter), K(Key.OemTilde, KeyModifiers.Alt), K(Key.OemOpenBrackets), K(Key.F12, KeyModifiers.Shift)]);
        foreach (var combo in combos)
        {
            Assert.True(KeyCombo.TryParse(combo.ToString(), out var parsed), combo.ToString());
            Assert.Equal(combo, parsed);
        }
        Assert.Equal("Ctrl OemPlus", K(Key.OemPlus, KeyModifiers.Control).ToString());
        Assert.Equal("Shift Left", K(Key.Left, KeyModifiers.Shift).ToString());
        Assert.Equal("Enter", K(Key.Return).ToString());

        Assert.True(KeyCombo.TryParse("Ctrl+Shift+Z", out var redo));
        Assert.Equal(K(Key.Z, KeyModifiers.Control | KeyModifiers.Shift), redo);
        Assert.True(KeyCombo.TryParse("control  shift s", out var saveAs));
        Assert.Equal(K(Key.S, KeyModifiers.Control | KeyModifiers.Shift), saveAs);
        foreach (string? bad in new[] { null, "", "Ctrl", "Foo", "Ctrl Foo", "5", "Meta K", "Ctrl 65", "A,B", "LeftCtrl" })
            Assert.False(KeyCombo.TryParse(bad, out _), bad);
    }

    [Fact]
    public void Assign_replaces_all_keys_and_takes_the_key_from_others()
    {
        var map = new KeyMap();

        map.Assign(ShortcutAction.Redo, K(Key.Y, KeyModifiers.Control));
        Assert.Equal(["Ctrl Y"], Labels(map, ShortcutAction.Redo));
        Assert.False(map.IsDefault(ShortcutAction.Redo));

        map.Assign(ShortcutAction.ToggleExclude, K(Key.E, KeyModifiers.Control));
        Assert.Empty(map[ShortcutAction.Export]);
        Assert.Equal(["Ctrl E"], Labels(map, ShortcutAction.ToggleExclude));
    }

    [Fact]
    public void FindConflict_ignores_the_action_itself()
    {
        var map = new KeyMap();
        var ctrlE = K(Key.E, KeyModifiers.Control);

        Assert.Null(map.FindConflict(ctrlE, ShortcutAction.Export));
        Assert.Equal(ShortcutAction.Export, map.FindConflict(ctrlE, ShortcutAction.ToggleExclude));
        Assert.Equal(ShortcutAction.PlayPause, map.Find(K(Key.Space)));
        Assert.Equal(ShortcutAction.DeleteClip, map.Find(K(Key.Back)));
        Assert.Null(map.Find(K(Key.Q)));
    }

    [Fact]
    public void Reset_takes_the_default_keys_back()
    {
        var map = new KeyMap();
        map.Assign(ShortcutAction.Redo, K(Key.Y, KeyModifiers.Control));
        map.Assign(ShortcutAction.ToggleExclude, K(Key.E, KeyModifiers.Control));

        map.Reset(ShortcutAction.Export);

        Assert.Equal(["Ctrl E"], Labels(map, ShortcutAction.Export));
        Assert.Empty(map[ShortcutAction.ToggleExclude]);

        map.ResetAll();
        Assert.All(Enum.GetValues<ShortcutAction>(), a => Assert.True(map.IsDefault(a)));
    }

    [Fact]
    public void Only_changed_actions_are_saved_and_read_back()
    {
        var map = new KeyMap();
        Assert.Null(map.ToSettings().Shortcuts);

        map.Assign(ShortcutAction.ToggleExclude, K(Key.E, KeyModifiers.Control));
        map.Assign(ShortcutAction.ZoomIn, K(Key.OemPlus, KeyModifiers.Control | KeyModifiers.Shift));
        var saved = map.ToSettings().Shortcuts;

        Assert.NotNull(saved);
        Assert.Equal(["ToggleExclude", "Export", "ZoomIn"], saved.Keys);
        Assert.Equal(["Ctrl E"], saved["ToggleExclude"]);
        Assert.Empty(saved["Export"]);
        Assert.Equal(["Ctrl Shift OemPlus"], saved["ZoomIn"]);

        var loaded = new KeyMap();
        loaded.Load(map.ToSettings());
        foreach (var action in Enum.GetValues<ShortcutAction>())
            Assert.Equal(map[action], loaded[action]);

        var messy = new KeyMap();
        messy.Load(new KeyboardSettings(new Dictionary<string, IReadOnlyList<string>>
        {
            ["Nope"] = ["Ctrl K"],
            ["3"] = ["Ctrl K"],
            ["undo"] = ["Ctrl K"],
            ["Save"] = ["Ctrl Foo", "Ctrl K", "Ctrl+K"],
        }));
        Assert.Equal(["Ctrl K"], Labels(messy, ShortcutAction.Save));
        Assert.All(Enum.GetValues<ShortcutAction>().Where(a => a != ShortcutAction.Save), a => Assert.True(messy.IsDefault(a)));
    }

    [Fact]
    public void A_saved_key_wins_over_a_default_it_clashes_with()
    {
        var map = new KeyMap();
        map.Load(new KeyboardSettings(new Dictionary<string, IReadOnlyList<string>> { ["ToggleExclude"] = ["Ctrl E"] }));

        Assert.Equal(ShortcutAction.ToggleExclude, map.Find(K(Key.E, KeyModifiers.Control)));
        Assert.Empty(map[ShortcutAction.Export]);
    }

    [Fact]
    public void Changed_fires_once_per_real_change()
    {
        var map = new KeyMap();
        int changes = 0;
        map.Changed += (_, _) => changes++;

        map.Reset(ShortcutAction.Undo);
        map.ResetAll();
        map.Load(null);
        Assert.Equal(0, changes);

        map.Assign(ShortcutAction.ToggleExclude, K(Key.E, KeyModifiers.Control));
        Assert.Equal(1, changes);
        map.Assign(ShortcutAction.ToggleExclude, K(Key.E, KeyModifiers.Control));
        Assert.Equal(1, changes);

        map.ResetAll();
        Assert.Equal(2, changes);
    }
}
