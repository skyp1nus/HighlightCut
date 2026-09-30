using HighlightCut.App.ViewModels;
using HighlightCut.Core.Editing;
using HighlightCut.Core.Editing.Commands;
using HighlightCut.Core.Model;

namespace HighlightCut.App.Demo;

/// <summary>
/// Puts the editor into one of the design's screens with the prototype's sample data
/// (<c>viewState()</c>, <c>priorLog()</c> and <c>runAI()</c> in design/project/HighlightCut.dc.html).
/// Claude's scripted actions are real Core commands made with <see cref="EditOrigin.Assistant"/>.
/// </summary>
public static partial class DemoScenario
{
    public static void Apply(EditorViewModel editor, DesignScreen screen)
    {
        editor.IsDemo = true;
        editor.Export.Close();
        editor.Settings.Close();
        editor.Settings.Section = "Transcription";
        editor.Unload();
        editor.PlaceholderDuration = DesignSample.SampleDuration;
        editor.Claude.Log.Clear();
        editor.Claude.IsConnected = true;
        editor.Claude.IsOpen = screen == DesignScreen.Ai;
        editor.RecentFiles.Clear();
        editor.ToolStatus = "ffmpeg 7.1 · ready";
        editor.ZoomLevel = 0;
        // The design shows every marker layer (the user's own chips are not touched in demo mode).
        editor.ShowKeyframes = editor.ShowWaveform = editor.ShowSilences = editor.ShowScenes = editor.ShowFrames = editor.SnapToKeyframes = true;
        editor.Settings.LoadDemo();
        editor.Tour.Reset();

        if (screen is DesignScreen.Empty or DesignScreen.Welcome or DesignScreen.WelcomeClaude)
            editor.Claude.Recount();
        else
            ApplyEditor(editor, screen);

        ApplyTranscriptionMcp(editor, screen);
        ApplyTranscript(editor, screen);
        ApplyClaude(editor, screen);
        ApplyGeneralPlaybackExport(editor, screen);
        ApplyKeyboard(editor, screen);
        ApplyWelcome(editor, screen);
    }

    /// <summary>The welcome tour's screens: its first step, or Connect Claude before Claude has connected.</summary>
    private static void ApplyWelcome(EditorViewModel editor, DesignScreen screen)
    {
        if (screen is not (DesignScreen.Welcome or DesignScreen.WelcomeClaude))
            return;
        editor.Tour.Open();
        if (screen == DesignScreen.WelcomeClaude)
        {
            editor.Claude.IsConnected = false;
            editor.Claude.IsListening = true;
            editor.Tour.Step = 3;
        }
    }

    // Per-area hooks, implemented in DemoScenario.<Area>.cs; each runs for every screen, in this order.
    static partial void ApplyTranscriptionMcp(EditorViewModel editor, DesignScreen screen);
    static partial void ApplyTranscript(EditorViewModel editor, DesignScreen screen);
    static partial void ApplyClaude(EditorViewModel editor, DesignScreen screen);
    static partial void ApplyGeneralPlaybackExport(EditorViewModel editor, DesignScreen screen);
    static partial void ApplyKeyboard(EditorViewModel editor, DesignScreen screen);

    /// <summary>The settings section a screen shows (prototype <c>SET_VIEWS</c>); null when the dialog is closed.</summary>
    public static string? SettingsSection(DesignScreen screen) => screen switch
    {
        DesignScreen.Settings => "Transcription",
        DesignScreen.SettingsGeneral => "General",
        DesignScreen.SettingsPlayback => "Playback",
        DesignScreen.SettingsExport => "Export",
        DesignScreen.SettingsKeyboard or DesignScreen.SettingsKeyboardRecording or DesignScreen.SettingsKeyboardConflict => "Keyboard",
        DesignScreen.SettingsMcp => "MCP server",
        _ => null,
    };

    /// <summary>Every screen but Empty: the sample project, Claude's earlier edits and the screen's dialog.</summary>
    private static void ApplyEditor(EditorViewModel editor, DesignScreen screen)
    {
        editor.LoadProject(DesignSample.Project, new DesignSample(), DesignSample.SourceInfo);
        AddPriorLog(editor);

        if (screen == DesignScreen.Ai)
        {
            AddAiLog(editor);
            editor.Select(null);
            editor.Time = 268.4;
        }
        else if (screen is DesignScreen.Exporting)
        {
            editor.Select(null);
            editor.Time = 151.066;
        }
        else
        {
            editor.Select(editor.Find(2));
            editor.Time = 151.066;
        }
        editor.Claude.Recount();
        editor.ApplyClaudeHighlights();

        switch (screen)
        {
            case DesignScreen.Export:
                editor.Export.Open();
                break;
            case DesignScreen.Exporting:
                editor.Export.Open();
                editor.Export.Loop = true;
                editor.Export.Start(0.46);
                break;
        }
        if (SettingsSection(screen) is { } section)
        {
            editor.Settings.Section = section;
            editor.Settings.Open();
        }
    }

    /// <summary>Opens the sample project as a normal (non-demo) project.</summary>
    public static void OpenSample(EditorViewModel editor)
    {
        editor.LeaveDemo();
        editor.LoadProject(DesignSample.Project, new DesignSample(), DesignSample.SourceInfo);
    }

    /// <summary>What Claude did before the design's screens: labelled the clips and added the first one.</summary>
    private static void AddPriorLog(EditorViewModel editor)
    {
        var claude = editor.Claude;
        var session = editor.Session;
        var now = DateTimeOffset.Now;
        var labels = DesignSample.Project.Clips.ToDictionary(c => c.Id, c => c.Label);

        Clip? removed = null;
        claude.Add(new(ClaudeLogKind.Action, "Added clip 00:12–00:45", "Clip 1 · 33.200 s · first mention of “HighlightCut” in transcript",
            [1], setUndone: Safe(editor, undone =>
            {
                if (undone)
                {
                    removed = session.Project.Find(1);
                    if (removed is not null)
                        session.Execute(new RemoveClipCommand(1), EditOrigin.Assistant);
                }
                else if (removed is not null)
                {
                    session.Execute(new AddClipCommand(removed.Start, removed.End, removed.Label, 0, removed.Id, removed.IsIncluded,
                        removed.Color),
                        EditOrigin.Assistant);
                }
            }), at: now.AddMinutes(-21)));

        claude.Add(new(ClaudeLogKind.Action, "Labeled 5 clips from transcript", "Cold open, Setup walkthrough, Export demo, Q&A highlights, Outro",
            [1, 2, 3, 4, 5], setUndone: Safe(editor, undone =>
                session.Execute(new BatchCommand("set_label", "Labeled 5 clips from transcript",
                    [.. labels.Keys.Where(id => session.Project.Find(id) is not null)
                        .Select(id => (IEditCommand)new RenameClipCommand(id, undone ? "Clip " + session.Project.NumberOf(id) : labels[id]))]),
                    EditOrigin.Assistant)),
            at: now.AddMinutes(-18)));
    }

    /// <summary>The "AI editing" screen: Claude's three edits are applied and a fourth is in progress.</summary>
    private static void AddAiLog(EditorViewModel editor)
    {
        var claude = editor.Claude;
        var session = editor.Session;
        var now = DateTimeOffset.Now;

        var silences = new BatchCommand("trim_segment", "Removed 3 silences",
        [
            new SetClipRangeCommand(1, 12, 40.8),
            new SetClipRangeCommand(2, 124, 190),
            new SetClipRangeCommand(5, 750, 822),
        ]);
        var silencesEntry = session.Execute(silences, EditOrigin.Assistant)!;
        claude.Add(new(ClaudeLogKind.Action, "Removed 3 silences",
            "Trimmed 00:00:40.8–00:00:45.2, 00:01:58.4–00:02:04.0 and 00:13:42.0–00:13:48.8", [1, 2, 5],
            silencesEntry.OutputDelta, changed: true, setUndone: Safe(editor, undone => Toggle(session, silencesEntry, undone)), at: now.AddSeconds(-5)));

        int index = session.Project.IndexOf(3) + 1;
        var addEntry = session.Execute(new AddClipCommand(432, 460, "Key quote", index, 6), EditOrigin.Assistant)!;
        claude.Add(new(ClaudeLogKind.Action, "Added clip 07:12–07:40", "“Key quote” · transcript match for “the whole point is speed”",
            [6], addEntry.OutputDelta, changed: true, setUndone: Safe(editor, undone => Toggle(session, addEntry, undone)), at: now.AddSeconds(-3)));

        var trimEntry = session.Execute(new SetClipRangeCommand(3, 262.08, 361.32), EditOrigin.Assistant)!;
        claude.Add(new(ClaudeLogKind.Action, "Trimmed clip 3 out point", "00:06:05.520 → 00:06:01.320 (−4.200 s), cut before “um, so anyway”",
            [3], trimEntry.OutputDelta, changed: true, setUndone: Safe(editor, undone => Toggle(session, trimEntry, undone)), at: now.AddSeconds(-2)));

        claude.Add(new(ClaudeLogKind.Live, "Reviewing clip 5 for filler words…", "Reading transcript 00:08:40–00:10:12", [4]));
    }

    /// <summary>Runs a card's undo or redo; if a later edit conflicts, the card stays as it was and says why.</summary>
    private static Action<bool> Safe(EditorViewModel editor, Action<bool> setUndone) => undone =>
    {
        try
        {
            setUndone(undone);
        }
        catch (EditException e)
        {
            editor.ShowMessage(e.Message);
            throw;
        }
    };

    /// <summary>Undo on a demo card reverts just that edit; Redo reverts the revert.</summary>
    private static void Toggle(EditorSession session, HistoryEntry entry, bool undone)
    {
        if (undone)
            session.Revert(entry, EditOrigin.Assistant);
        else if (session.RevertOf(entry) is { } revert)
            session.Revert(revert, EditOrigin.Assistant);
    }
}
