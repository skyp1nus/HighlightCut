using Avalonia.Headless.XUnit;
using HighlightCut.App.Demo;
using HighlightCut.App.ViewModels;
using HighlightCut.Core.Editing;
using HighlightCut.Core.Model;

namespace HighlightCut.App.Tests;

public class EditorViewModelTests
{
    private static EditorViewModel Sample() => App.CreateEditor(DesignScreen.Editing);

    [AvaloniaFact]
    public void Totals_match_the_design()
    {
        var editor = Sample();
        Assert.Equal("4:47.040", editor.TotalText);
        Assert.Equal("00:04:47.040", editor.OutputTimecode);
        Assert.Equal("4:47", editor.KeptText);
        Assert.Equal("9:45", editor.ExcludedText);
        Assert.Equal("4 of 5 clips", editor.ClipSummary);
        Assert.Equal("4 of 5 clips", editor.OutputSummary);
        Assert.Equal("14:32", editor.SourceLengthText);
        Assert.Equal("Clip 2  00:01:58.400 → 00:03:10.000  ·  1:11.600", editor.SelectionInfo);
    }

    [AvaloniaFact]
    public void A_project_saved_with_overlapping_clips_opens_and_counts_each_second_once()
    {
        var editor = App.CreateEditor(null);
        editor.LeaveDemo();
        var project = DesignSample.Project with
        {
            Clips = [new Clip(1, "Cold open", 12, 45.2), new Clip(2, "Again", 40, 60)],
        };

        editor.LoadProject(project, new DesignSample(), DesignSample.SourceInfo);

        Assert.Equal(2, editor.Clips.Count);
        Assert.Equal(48, editor.OutputDuration, 6);
        Assert.Contains("Clips 1 and 2 overlap", editor.StatusMessage);

        editor.Select(editor.Find(1));
        Assert.True(editor.CanJoinWithNext);
        editor.JoinWithNext();
        Assert.Empty(editor.Session.Project.Overlaps());
        Assert.Equal((12.0, 60.0), (editor.Find(1)!.Start, editor.Find(1)!.End));
    }

    [AvaloniaFact]
    public void Set_in_and_out_stop_at_the_neighbouring_clips()
    {
        var editor = Sample();
        var clip = editor.Find(2)!;
        var before = editor.Clips.Where(c => c.End <= clip.Start).MaxBy(c => c.End)!;
        editor.Select(clip);

        editor.SetTime(before.End - 5);
        editor.MarkIn();

        Assert.Equal(before.End, clip.Start);
        Assert.Empty(editor.Session.Project.Overlaps());
    }

    [AvaloniaFact]
    public void Set_in_without_a_selection_makes_a_clip_that_stops_at_the_next_one()
    {
        var editor = Sample();
        var next = editor.Find(2)!;
        editor.Select(null);
        editor.SetTime(next.Start - 4);

        editor.MarkIn();

        var added = editor.SelectedClip!;
        Assert.Equal((next.Start - 4, next.Start), (added.Start, added.End));

        // Inside a clip there is nothing to start: it says which clip to select instead.
        editor.Select(null);
        editor.SetTime(next.Start + 10);
        int count = editor.Clips.Count;
        editor.MarkIn();
        Assert.Equal(count, editor.Clips.Count);
        Assert.Contains($"in clip {next.Number}", editor.StatusMessage);
    }

    [AvaloniaFact]
    public void Opening_a_project_refreshes_everything_derived_from_its_source()
    {
        var editor = App.CreateEditor(null);
        var changed = new HashSet<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        DemoScenario.OpenSample(editor);

        Assert.Equal("00:14:32.480", editor.DurationText);
        Assert.Equal("14:32", editor.SourceLengthText);
        Assert.Superset(new HashSet<string?> { "DurationText", "SourceLengthText", "HasFile", "ExcludedText", "KeptText", "StatusRight" }, changed);
        Assert.Equal("lossless copy · snap on · not saved", editor.StatusRight);
    }

    [AvaloniaFact]
    public void Excluded_gaps_are_the_source_ranges_outside_all_clips()
    {
        var editor = Sample();
        var gaps = editor.ExcludedGaps();
        Assert.Equal(6, gaps.Count);
        Assert.Equal(new TimeRange(0, 12), gaps[0]);
        Assert.Equal(new TimeRange(45.2, 118.4), gaps[1]);
        Assert.Equal(new TimeRange(828.8, 872.48), gaps[^1]);
    }

    [AvaloniaFact]
    public void Keep_range_inserts_a_clip_in_source_order()
    {
        var editor = Sample();
        editor.KeepRange(45.2, 118.4);
        var added = editor.SelectedClip!;
        Assert.Equal(1, editor.Clips.IndexOf(added));
        Assert.Equal("Clip 6", added.Label);
        Assert.Equal(5, editor.ExcludedGaps().Count);
    }

    [AvaloniaFact]
    public void Trim_keeps_the_minimum_clip_length()
    {
        var editor = Sample();
        var clip = editor.Clips[0];
        editor.Trim(clip, inPoint: true, clip.End + 10);
        Assert.Equal(clip.End - EditRules.MinClipDuration, clip.Start, 6);
        editor.Trim(clip, inPoint: false, 0);
        Assert.Equal(clip.Start + EditRules.MinClipDuration, clip.End, 6);
    }

    [AvaloniaFact]
    public void Trim_snaps_to_keyframes_unless_snapping_is_off()
    {
        var editor = Sample();
        var clip = editor.Clips[1];
        double k = editor.Media!.Keyframes.First(x => x > clip.Start + 1);
        editor.Trim(clip, inPoint: true, k + 0.2, snapThreshold: 0.5);
        Assert.Equal(k, clip.Start);
        editor.SnapToKeyframes = false;
        editor.Trim(clip, inPoint: true, k + 0.2, snapThreshold: 0.5);
        Assert.Equal(k + 0.2, clip.Start, 9);
    }

    [AvaloniaFact]
    public void One_drag_is_one_undo_step()
    {
        var editor = Sample();
        var clip = editor.Clips[0];
        editor.SnapToKeyframes = false;
        foreach (double t in new[] { 11.0, 10.0, 9.0 })
            editor.Trim(clip, inPoint: true, t, mergeKey: "drag");
        Assert.Equal(9, clip.Start);
        editor.Undo();
        Assert.Equal(12, clip.Start);
    }

    [AvaloniaFact]
    public void Edits_go_through_the_core_session_and_can_be_undone()
    {
        var editor = App.CreateEditor(null);
        DemoScenario.OpenSample(editor);
        var clip = editor.Clips[0];
        editor.Select(clip);

        editor.ToggleExclude();
        Assert.False(clip.IsIncluded);
        Assert.False(editor.Session.Project.Get(clip.Id).IsIncluded);
        Assert.True(editor.CanUndo);

        editor.Undo();
        Assert.True(clip.IsIncluded);
        editor.Redo();
        Assert.False(clip.IsIncluded);
    }

    [AvaloniaFact]
    public void Outside_demo_mode_the_claude_panel_lists_the_edit_history()
    {
        var editor = App.CreateEditor(null);
        DemoScenario.OpenSample(editor);
        editor.Select(editor.Clips[0]);
        editor.ToggleExclude();
        editor.MoveClip(0, 2);

        Assert.Equal(["Excluded clip 1", "Moved clip 1 to position 3"], editor.Claude.Log.Select(a => a.Text));
        Assert.Equal("set_included · −33.20 s", editor.Claude.Log[0].Meta);
        var first = editor.Claude.Log[0];

        // Undo on a card reverts just that entry, as a new edit; the later move stays.
        first.UndoCommand.Execute(null);
        Assert.True(first.IsUndone);
        Assert.True(editor.Session.Project.Get(1).IsIncluded);
        Assert.Equal(2, editor.Session.Project.IndexOf(1));
        Assert.Equal("Reverted “Excluded clip 1”", editor.Claude.Log[^1].Text);
        Assert.False(editor.Claude.Log[1].IsUndone);

        // Ctrl+Z undoes the revert, which brings the card back.
        editor.Undo();
        Assert.False(first.IsUndone);
        Assert.False(editor.Session.Project.Get(1).IsIncluded);

        // Undo on an earlier card that conflicts with a later edit is refused with a message.
        editor.Select(editor.Find(1));
        editor.ToggleExclude();
        first.UndoCommand.Execute(null);
        Assert.False(first.IsUndone);
        Assert.NotNull(editor.StatusMessage);
    }

    [AvaloniaFact]
    public void Delete_removes_the_clip_and_selects_the_next_one()
    {
        var editor = Sample();
        var selected = editor.SelectedClip!;
        int index = editor.Clips.IndexOf(selected);
        editor.DeleteClip();
        Assert.DoesNotContain(selected, editor.Clips);
        Assert.Same(editor.Clips[index], editor.SelectedClip);
        editor.Undo();
        Assert.Equal(5, editor.Clips.Count);
    }

    [AvaloniaFact]
    public void The_remove_button_of_a_row_removes_that_clip()
    {
        var editor = Sample();
        var selected = editor.SelectedClip!;
        var other = editor.Clips[0];
        editor.RemoveClipCommand.Execute(other);
        Assert.DoesNotContain(other, editor.Clips);
        Assert.Same(selected, editor.SelectedClip);
    }

    [AvaloniaFact]
    public void The_split_tool_cuts_the_clip_where_it_is_clicked()
    {
        var editor = Sample();
        editor.SplitToolCommand.Execute(null);
        Assert.True(editor.IsSplitTool);
        var clip = editor.Find(3)!;
        editor.SplitAt(clip, 300);
        Assert.Equal(300, clip.End);
        Assert.Equal(300, editor.SelectedClip!.Start);
        Assert.Equal(6, editor.Clips.Count);
    }

    [AvaloniaFact]
    public void Marker_layers_and_snapping_toggle_from_the_toolbar()
    {
        var editor = Sample();
        Assert.True(editor.HasSilenceData);
        Assert.True(editor.HasSceneData);
        editor.ToggleSilencesCommand.Execute(null);
        editor.ToggleScenesCommand.Execute(null);
        editor.ToggleKeyframesCommand.Execute(null);
        editor.ToggleSnapCommand.Execute(null);
        Assert.False(editor.ShowSilences || editor.ShowScenes || editor.ShowKeyframes || editor.SnapToKeyframes);

        // Real files have no silence or scene detection yet.
        var real = App.CreateEditor(null, new SampleOpener());
        Assert.False(real.HasSilenceData);
    }

    [AvaloniaFact]
    public void The_mcp_badge_follows_the_connection_and_claudes_work()
    {
        Assert.Equal("MCP · Claude connected", Sample().McpText);
        Assert.Equal("MCP · Claude editing", App.CreateEditor(DesignScreen.Ai).McpText);
        Assert.Equal("MCP · Off", App.CreateEditor(null).McpText);
    }

    [AvaloniaFact]
    public void Invalid_edits_show_a_message_instead_of_failing()
    {
        var editor = Sample();
        editor.SetTime(editor.Duration - 0.05);
        editor.Select(null);
        editor.MarkIn();
        Assert.Equal(5, editor.Clips.Count);
        Assert.NotNull(editor.StatusMessage);
        Assert.Equal(editor.StatusMessage, editor.StatusRight);
    }

    [AvaloniaFact]
    public void Claude_undo_restores_the_previous_values_and_clears_the_highlight()
    {
        var editor = App.CreateEditor(DesignScreen.Ai);
        var setup = editor.Clips.Single(c => c.Label == "Setup walkthrough");
        Assert.True(setup.IsAiChanged);
        Assert.Equal("+7.00 s by Claude", editor.ClaudeDeltaText);
        Assert.True(editor.Find(3)!.IsAiRecent);
        Assert.True(editor.Find(4)!.IsAiWorking);

        // Undo on "Removed 3 silences" reverts only that action: the later trim of clip 3 stays.
        var silences = editor.Claude.Log.Single(a => a.Text == "Removed 3 silences");
        silences.UndoCommand.Execute(null);

        Assert.Equal(118.4, setup.Start);
        Assert.Equal(45.2, editor.Find(1)!.End);
        Assert.Equal(361.32, editor.Find(3)!.End);
        Assert.False(setup.IsAiChanged);
        Assert.Equal("+23.80 s by Claude", editor.ClaudeDeltaText);
        Assert.True(silences.ShowUndone);
        Assert.False(silences.ShowUndo);
    }

    [AvaloniaFact]
    public void Export_paths_and_steps_follow_the_settings()
    {
        var editor = Sample();
        var export = editor.Export;
        export.Open();
        Assert.Equal(@"C:\Users\You\Videos\Exports\interview_final_v3-cut.mp4", export.OutputPath);
        Assert.Equal("1 file", export.FileCountText);
        Assert.Equal("4 clips · 00:04:47.040", export.HeaderSummary);
        Assert.Equal("interview_final_v3-cut.mp4", export.FileNamesText);
        Assert.StartsWith("3 of 4 in points will snap back", export.SnapNote, StringComparison.Ordinal);

        export.Merge = false;
        export.Container = "MKV";
        Assert.Equal("4 files", export.FileCountText);
        Assert.Equal("Separate files (4)", export.SeparateFilesText);
        Assert.EndsWith(@"interview_final_v3-cut-{n}.mkv", export.OutputPath);
        Assert.Equal("interview_final_v3-cut-01.mkv … interview_final_v3-cut-04.mkv", export.FileNamesText);

        export.Start(0.5);
        Assert.Equal(4, export.Rows.Count);
        Assert.Equal("interview_final_v3-cut-03.mkv", export.Rows[2].Name);
        Assert.Equal("Writing clip 3 of 4", export.ProgressText);
        Assert.Single(export.Rows, r => r.IsCurrent);

        // Cancel export goes back to the settings.
        export.CancelExport();
        Assert.True(export.IsConfiguring);
        export.Close();
    }

    [AvaloniaFact]
    public void Export_names_follow_the_file_name_pattern()
    {
        var editor = Sample();
        editor.Settings.FileNamePattern = "{date}_{project}_{label}";
        var export = editor.Export;
        export.Open();

        Assert.Equal("2026-09-25_interview_final_v3.mp4", export.FileNamesText);
        export.Merge = false;
        Assert.Equal("2026-09-25_interview_final_v3_cold-open.mp4 … 2026-09-25_interview_final_v3_outro.mp4", export.FileNamesText);
        Assert.EndsWith("{date}_interview_final_v3_{label}.mp4", export.OutputPath, StringComparison.Ordinal);
        export.Close();
    }

    [AvaloniaFact]
    public void Smart_cut_is_listed_but_cannot_be_picked()
    {
        var export = Sample().Export;
        var smart = export.Modes.Single(m => m.Mode == ExportMode.Smart);
        smart.PickCommand.Execute(null);
        Assert.False(smart.IsAvailable);
        Assert.Equal(ExportMode.Copy, export.Mode);
        Assert.DoesNotContain(smart, export.ModeCards);
    }
}
