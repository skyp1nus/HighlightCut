using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OurCut.App.Demo;
using OurCut.App.Services;
using OurCut.App.ViewModels;
using OurCut.App.Views;
using OurCut.App.Views.Settings;

namespace OurCut.App.Tests;

/// <summary>Settings → Keyboard: search, recording a key, conflicts, and saving the shortcuts.</summary>
public sealed class KeyboardSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ourcut-keyboard").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static (MainWindow Window, EditorViewModel Editor) Open(DesignScreen screen)
    {
        var editor = App.CreateEditor(screen);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Pump();
        return (window, editor);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        window.KeyPress(key, mods, PhysicalKey.None, null);
        window.KeyRelease(key, mods, PhysicalKey.None, null);
        Pump();
    }

    private static KeyboardSettingsViewModel Keyboard() => App.CreateEditor(null).Settings.Keyboard;

    private static string[] Chips(ShortcutRowViewModel row) => [.. row.Chips.Select(c => c.Label)];

    private static ShortcutRowViewModel[] Visible(KeyboardSettingsViewModel keyboard) =>
        [.. keyboard.Groups.Where(g => g.IsMatch).SelectMany(g => g.Rows).Where(r => r.IsMatch)];

    [AvaloniaFact]
    public void Search_filters_rows_by_name_and_keys()
    {
        var keyboard = Keyboard();

        keyboard.Query = "zoom";
        Assert.Equal(["View"], keyboard.Groups.Where(g => g.IsMatch).Select(g => g.Label));
        Assert.Equal(["Zoom in", "Zoom out"], Visible(keyboard).Select(r => r.Name));
        Assert.False(keyboard.Row(ShortcutAction.FitTimeline).IsMatch);

        keyboard.Query = "shift ←";
        Assert.Equal(["Jump back"], Visible(keyboard).Select(r => r.Name));

        keyboard.Query = "  undo ";
        Assert.Equal(["Undo"], Visible(keyboard).Select(r => r.Name));
        Assert.False(keyboard.IsEmpty);

        keyboard.Query = "xyz";
        Assert.True(keyboard.IsEmpty);
        Assert.Empty(Visible(keyboard));
        Assert.Equal("No shortcuts match “xyz”", keyboard.EmptyText);

        keyboard.Query = "";
        Assert.Equal(KeyMap.Catalog.Count, Visible(keyboard).Length);
    }

    [AvaloniaFact]
    public void Selecting_a_row_shows_Change_and_Reset()
    {
        var keyboard = Keyboard();
        var exclude = keyboard.Row(ShortcutAction.ToggleExclude);
        var undo = keyboard.Row(ShortcutAction.Undo);

        exclude.SelectCommand.Execute(null);

        Assert.Same(exclude, keyboard.Selected);
        Assert.True(exclude.IsSelected);
        Assert.True(exclude.ShowActions);
        Assert.True(exclude.ShowKeys);
        Assert.Equal(0.4, exclude.ResetOpacity);
        Assert.False(undo.ShowActions);

        undo.SelectCommand.Execute(null);
        Assert.False(exclude.IsSelected);
        Assert.True(undo.IsSelected);
    }

    [AvaloniaFact]
    public void Recording_sets_the_new_shortcut()
    {
        var keyboard = Keyboard();
        var exclude = keyboard.Row(ShortcutAction.ToggleExclude);

        exclude.ChangeCommand.Execute(null);
        Assert.True(exclude.IsRecording);
        Assert.True(exclude.ShowPill);
        Assert.False(exclude.ShowKeys);
        Assert.False(exclude.ShowActions);
        Assert.Equal("Press the new shortcut…", exclude.PillText);

        keyboard.Record(Key.K, KeyModifiers.Control | KeyModifiers.Shift);

        Assert.False(keyboard.IsRecording);
        Assert.Equal(["Ctrl Shift K"], Chips(exclude));
        Assert.True(exclude.IsModified);
        Assert.Equal(1, exclude.ResetOpacity);
        Assert.True(exclude.ShowActions);

        exclude.ResetCommand.Execute(null);
        Assert.Equal(["E"], Chips(exclude));
        Assert.False(exclude.IsModified);
    }

    [AvaloniaFact]
    public void Modifiers_alone_keep_recording_and_Escape_cancels()
    {
        var keyboard = Keyboard();
        var redo = keyboard.Row(ShortcutAction.Redo);
        redo.ChangeCommand.Execute(null);

        keyboard.Record(Key.LeftCtrl, KeyModifiers.Control);
        keyboard.Record(Key.LeftShift, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.True(keyboard.IsRecording);

        keyboard.Record(Key.Escape, KeyModifiers.Shift);
        Assert.False(keyboard.IsRecording);
        Assert.Equal(["Ctrl Y", "Ctrl Shift Z"], Chips(redo));
        Assert.Same(redo, keyboard.Selected);
    }

    [AvaloniaFact]
    public void A_taken_shortcut_shows_the_conflict()
    {
        var keyboard = Keyboard();
        var exclude = keyboard.Row(ShortcutAction.ToggleExclude);
        exclude.ChangeCommand.Execute(null);

        keyboard.Record(Key.E, KeyModifiers.Control);

        Assert.False(keyboard.IsRecording);
        Assert.Equal(new ShortcutConflict(new KeyCombo(Key.E, KeyModifiers.Control), ShortcutAction.Export), keyboard.Conflict);
        Assert.True(exclude.HasConflict);
        Assert.True(exclude.ShowPill);
        Assert.False(exclude.IsRecording);
        Assert.Equal("Ctrl E", exclude.PillText);
        Assert.Equal("Ctrl+E is used by Export.", exclude.ConflictText);
        Assert.False(exclude.ShowActions);
        Assert.Equal(["E"], Chips(exclude));
        Assert.Equal(["Ctrl E"], Chips(keyboard.Row(ShortcutAction.Export)));
        Assert.False(keyboard.Row(ShortcutAction.Export).HasConflict);
    }

    [AvaloniaFact]
    public void Replace_moves_the_shortcut()
    {
        var keyboard = Keyboard();
        var exclude = keyboard.Row(ShortcutAction.ToggleExclude);
        var export = keyboard.Row(ShortcutAction.Export);
        exclude.ChangeCommand.Execute(null);
        keyboard.Record(Key.E, KeyModifiers.Control);

        exclude.ReplaceCommand.Execute(null);

        Assert.False(keyboard.HasConflict);
        Assert.True(export.IsNotSet);
        Assert.False(export.ShowKeys);
        Assert.True(export.IsModified);
        Assert.Equal(["Ctrl E"], Chips(exclude));
        Assert.True(exclude.ShowActions);
    }

    [AvaloniaFact]
    public void Cancel_keeps_both()
    {
        var keyboard = Keyboard();
        var exclude = keyboard.Row(ShortcutAction.ToggleExclude);
        exclude.ChangeCommand.Execute(null);
        keyboard.Record(Key.E, KeyModifiers.Control);

        exclude.CancelConflictCommand.Execute(null);

        Assert.False(keyboard.HasConflict);
        Assert.Equal(["E"], Chips(exclude));
        Assert.Equal(["Ctrl E"], Chips(keyboard.Row(ShortcutAction.Export)));
        Assert.True(exclude.ShowActions);
    }

    [AvaloniaFact]
    public void Reset_all_restores_the_defaults_and_ends_recording()
    {
        var keyboard = Keyboard();
        var exclude = keyboard.Row(ShortcutAction.ToggleExclude);
        exclude.ChangeCommand.Execute(null);
        keyboard.Record(Key.E, KeyModifiers.Control);
        exclude.ReplaceCommand.Execute(null);
        keyboard.Row(ShortcutAction.Undo).ChangeCommand.Execute(null);

        keyboard.ResetAllCommand.Execute(null);

        Assert.False(keyboard.IsRecording);
        Assert.All(keyboard.Rows, r => Assert.False(r.IsModified));
        Assert.Equal(["Ctrl E"], Chips(keyboard.Row(ShortcutAction.Export)));
        Assert.Same(keyboard.Row(ShortcutAction.Undo), keyboard.Selected);
    }

    [AvaloniaFact]
    public void Closing_or_switching_section_stops_recording()
    {
        var settings = App.CreateEditor(null).Settings;
        var keyboard = settings.Keyboard;
        settings.Section = "Keyboard";
        settings.Open();
        var exclude = keyboard.Row(ShortcutAction.ToggleExclude);

        exclude.ChangeCommand.Execute(null);
        settings.Close();
        Assert.False(keyboard.IsRecording);
        Assert.Same(exclude, keyboard.Selected);

        settings.Open();
        exclude.ChangeCommand.Execute(null);
        keyboard.Record(Key.E, KeyModifiers.Control);
        keyboard.Query = "ex";
        settings.Section = "General";
        Assert.Null(keyboard.Conflict);
        Assert.Same(exclude, keyboard.Selected);
        Assert.Equal("ex", keyboard.Query);
    }

    [AvaloniaFact]
    public void Shortcuts_are_saved_as_they_change_and_read_back()
    {
        var store = new AppSettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = App.CreateEditor(null).Settings;
        settings.Store = store;
        var keyboard = settings.Keyboard;
        Assert.Null(settings.Current.Keyboard);

        keyboard.Row(ShortcutAction.Redo).ChangeCommand.Execute(null);
        keyboard.Record(Key.K, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal(["Ctrl Shift K"], store.Load().Keyboard?.Shortcuts?["Redo"]);

        keyboard.Row(ShortcutAction.ToggleExclude).ChangeCommand.Execute(null);
        keyboard.Record(Key.E, KeyModifiers.Control);
        keyboard.Row(ShortcutAction.ToggleExclude).ReplaceCommand.Execute(null);

        var saved = store.Load().Keyboard?.Shortcuts;
        Assert.NotNull(saved);
        Assert.Equal(["ToggleExclude", "Redo", "Export"], saved.Keys);
        Assert.Equal(["Ctrl E"], saved["ToggleExclude"]);
        Assert.Empty(saved["Export"]);

        var reloaded = App.CreateEditor(null).Settings;
        reloaded.Load(store.Load());
        Assert.Equal(["Ctrl E"], Chips(reloaded.Keyboard.Row(ShortcutAction.ToggleExclude)));
        Assert.True(reloaded.Keyboard.Row(ShortcutAction.ToggleExclude).IsModified);
        Assert.True(reloaded.Keyboard.Row(ShortcutAction.Export).IsNotSet);
        Assert.Equal(ShortcutAction.Redo, reloaded.KeyMap.Find(new KeyCombo(Key.K, KeyModifiers.Control | KeyModifiers.Shift)));

        keyboard.ResetAllCommand.Execute(null);
        Assert.Null(store.Load().Keyboard);
    }

    [AvaloniaFact]
    public void Loading_other_settings_keeps_the_shortcuts_out_of_the_file()
    {
        var store = new AppSettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = App.CreateEditor(null).Settings;
        settings.Load(AppSettings.Default with { Keyboard = new KeyboardSettings() });
        settings.Store = store;

        settings.Device = "CPU";

        Assert.Null(store.Load().Keyboard);
    }

    [AvaloniaFact]
    public void The_window_records_keys_even_from_the_search_box()
    {
        var (window, editor) = Open(DesignScreen.SettingsKeyboard);
        var keyboard = editor.Settings.Keyboard;
        var section = window.GetVisualDescendants().OfType<KeyboardSection>().Single();
        var search = section.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "Search");
        search.Focus();
        Pump();
        Assert.Same(search, window.FocusManager?.GetFocusedElement());

        keyboard.Row(ShortcutAction.ToggleExclude).ChangeCommand.Execute(null);
        Pump();
        Assert.IsNotType<TextBox>(window.FocusManager?.GetFocusedElement());

        Press(window, Key.K, RawInputModifiers.Control);
        Assert.Equal(["Ctrl K"], Chips(keyboard.Row(ShortcutAction.ToggleExclude)));

        keyboard.Row(ShortcutAction.SetIn).ChangeCommand.Execute(null);
        Pump();
        window.KeyPress(Key.U, RawInputModifiers.None, PhysicalKey.None, "u");
        window.KeyTextInput("u");
        window.KeyRelease(Key.U, RawInputModifiers.None, PhysicalKey.None, "u");
        Pump();
        Assert.Equal(["U"], Chips(keyboard.Row(ShortcutAction.SetIn)));
        Assert.Equal("", keyboard.Query);
        Assert.True(editor.Settings.IsOpen);

        keyboard.Row(ShortcutAction.SetOut).ChangeCommand.Execute(null);
        search.Focus();
        Pump();
        Assert.False(keyboard.IsRecording);
        window.Close();
    }

    [AvaloniaFact]
    public void Escape_cancels_a_conflict_before_it_closes_the_settings()
    {
        var (window, editor) = Open(DesignScreen.SettingsKeyboardConflict);
        Assert.True(editor.Settings.Keyboard.HasConflict);

        Press(window, Key.Escape);
        Assert.False(editor.Settings.Keyboard.HasConflict);
        Assert.True(editor.Settings.IsOpen);

        Press(window, Key.Escape);
        Assert.False(editor.Settings.IsOpen);
        window.Close();

        (window, editor) = Open(DesignScreen.SettingsKeyboardRecording);
        Assert.True(editor.Settings.Keyboard.IsRecording);

        Press(window, Key.Escape);
        Assert.False(editor.Settings.Keyboard.IsRecording);
        Assert.True(editor.Settings.IsOpen);
        Assert.Equal(["E"], Chips(editor.Settings.Keyboard.Row(ShortcutAction.ToggleExclude)));

        Press(window, Key.Escape);
        Assert.False(editor.Settings.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Recording_does_not_run_the_editor_shortcuts()
    {
        var (window, editor) = Open(DesignScreen.SettingsKeyboardRecording);
        int clips = editor.Clips.Count;

        Press(window, Key.Delete);

        Assert.Equal(clips, editor.Clips.Count);
        Assert.True(editor.Settings.Keyboard.HasConflict);
        Assert.Equal(ShortcutAction.DeleteClip, editor.Settings.Keyboard.Conflict?.Other);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(DesignScreen.SettingsKeyboard)]
    [InlineData(DesignScreen.SettingsKeyboardRecording)]
    [InlineData(DesignScreen.SettingsKeyboardConflict)]
    public void Demo_screens_match_the_prototype_states(DesignScreen screen)
    {
        var settings = App.CreateEditor(screen).Settings;
        var keyboard = settings.Keyboard;
        var exclude = keyboard.Row(ShortcutAction.ToggleExclude);

        Assert.True(settings.IsOpen);
        Assert.Equal("Keyboard", settings.Section);
        Assert.Equal("", keyboard.Query);
        Assert.All(keyboard.Rows, r => Assert.False(r.IsModified));
        switch (screen)
        {
            case DesignScreen.SettingsKeyboard:
                Assert.Null(keyboard.Selected);
                Assert.False(keyboard.IsRecording);
                break;
            case DesignScreen.SettingsKeyboardRecording:
                Assert.True(exclude.IsRecording);
                Assert.False(exclude.HasConflict);
                break;
            default:
                Assert.False(keyboard.IsRecording);
                Assert.True(exclude.HasConflict);
                Assert.Equal(ShortcutAction.Export, keyboard.Conflict?.Other);
                Assert.Equal("Ctrl E", exclude.PillText);
                break;
        }
    }

    [AvaloniaFact]
    public void Every_demo_screen_starts_from_the_default_shortcuts()
    {
        var editor = App.CreateEditor(DesignScreen.SettingsKeyboardConflict);
        editor.Settings.Keyboard.Row(ShortcutAction.ToggleExclude).ReplaceCommand.Execute(null);
        editor.Settings.Keyboard.Query = "ex";

        DemoScenario.Apply(editor, DesignScreen.Editing);

        Assert.All(editor.Settings.Keyboard.Rows, r => Assert.False(r.IsModified));
        Assert.Null(editor.Settings.Keyboard.Selected);
        Assert.Equal("", editor.Settings.Keyboard.Query);
    }
}
