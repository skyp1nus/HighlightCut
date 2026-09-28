using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.Demo;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;

namespace HighlightCut.App.Tests;

/// <summary>The editor's keys come from Settings → Keyboard, and so do the hints that show them.</summary>
public class ShortcutDispatchTests
{
    [AvaloniaFact]
    public void A_changed_key_runs_its_action_and_the_old_key_nothing()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        int clips = editor.Clips.Count;
        editor.SetTime(editor.Clips[0].Start + 1);
        editor.Settings.KeyMap.Assign(ShortcutAction.Split, new KeyCombo(Key.K, KeyModifiers.Control));

        Assert.False(Shortcuts.Handle(editor, Key.S, KeyModifiers.None));
        Assert.Equal(clips, editor.Clips.Count);
        Assert.True(Shortcuts.Handle(editor, Key.K, KeyModifiers.Control));
        Assert.Equal(clips + 1, editor.Clips.Count);

        // Shift and a key without an action of its own: the key's action.
        Assert.True(Shortcuts.Handle(editor, Key.K, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal(clips + 1, editor.Clips.Count);

        // A key taken from another action now runs the new one.
        editor.Settings.KeyMap.Assign(ShortcutAction.Undo, new KeyCombo(Key.Space, KeyModifiers.None));
        bool playing = editor.IsPlaying;
        Assert.True(Shortcuts.Handle(editor, Key.Space, KeyModifiers.None));
        Assert.Equal(playing, editor.IsPlaying);
        Assert.Equal(clips, editor.Clips.Count);
    }

    [AvaloniaFact]
    public void Every_action_in_the_catalog_runs_from_its_default_key()
    {
        foreach (var info in KeyMap.Catalog)
        {
            var editor = App.CreateEditor(DesignScreen.Editing);
            // The file actions open pickers, which need a window; the others must be handled.
            if (info.Action is ShortcutAction.OpenVideo or ShortcutAction.OpenProject or ShortcutAction.Save or ShortcutAction.SaveAs)
                continue;
            var key = info.Defaults[0];
            Assert.True(Shortcuts.Handle(editor, key.Key, key.Modifiers), info.Name);
        }
    }

    [AvaloniaFact]
    public void The_zoom_keys_zoom_the_timeline()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        editor.ZoomLevel = 0.5;

        Shortcuts.Handle(editor, Key.OemPlus, KeyModifiers.Control);
        Assert.Equal(0.6, editor.ZoomLevel, 6);
        Shortcuts.Handle(editor, Key.Subtract, KeyModifiers.Control);
        Assert.Equal(0.5, editor.ZoomLevel, 6);
        Shortcuts.Handle(editor, Key.D0, KeyModifiers.Control);
        Assert.Equal(0, editor.ZoomLevel);
    }

    [AvaloniaFact]
    public void Without_a_video_only_opening_works()
    {
        var editor = App.CreateEditor(null);
        Assert.False(Shortcuts.Handle(editor, Key.Space, KeyModifiers.None));
        Assert.False(Shortcuts.Handle(editor, Key.S, KeyModifiers.None));
    }

    [AvaloniaFact]
    public void The_hints_show_the_keys_from_the_map()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        var keys = editor.Settings.Keys;
        Assert.Equal(("Space", "I / O", "← / →", "Del", "Ctrl Z", "Ctrl O"),
            (keys.PlayPause, keys.InOut, keys.FrameStep, keys.DeleteClip, keys.Undo, keys.OpenVideo));
        Assert.Equal(("Shift ←", "Shift →"), (keys.JumpBack, keys.JumpForward));
        var changed = new List<string?>();
        keys.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.Settings.KeyMap.Assign(ShortcutAction.SetIn, new KeyCombo(Key.J, KeyModifiers.None));
        // Taking Space leaves Play / pause without a key.
        editor.Settings.KeyMap.Assign(ShortcutAction.JumpBack, new KeyCombo(Key.Space, KeyModifiers.Alt));
        editor.Settings.KeyMap.Assign(ShortcutAction.Split, new KeyCombo(Key.Space, KeyModifiers.None));

        Assert.Equal(("J", "J / O", "Alt Space", "—"), (keys.SetIn, keys.InOut, keys.JumpBack, keys.PlayPause));
        Assert.Contains(nameof(ShortcutLabels.InOut), changed);
        Assert.Contains(nameof(ShortcutLabels.PlayPause), changed);

        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var status = window.GetVisualDescendants().OfType<StatusBar>().Single();
        Assert.Contains(status.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "J / O");
        window.Close();
    }
}
