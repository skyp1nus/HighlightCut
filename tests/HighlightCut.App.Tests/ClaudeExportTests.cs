using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using HighlightCut.App.Demo;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Tests;

/// <summary>Claude's export: the request banner, the card on top of the Claude panel, the pill and the MCP badge.</summary>
public class ClaudeExportTests
{
    [AvaloniaFact]
    public void The_request_shows_what_Claude_wants_to_export()
    {
        var editor = App.CreateEditor(DesignScreen.ClaudeRequest);
        var claude = editor.Claude;
        var card = claude.Export;

        Assert.True(card.IsAsking);
        Assert.Equal("Claude wants to export 4 clips (4:47)", card.RequestTitle);
        Assert.Equal(@"to D:\Videos\keynote-cut.mp4 · Lossless · MP4", card.RequestDetail);
        Assert.Equal(ClaudeExportStage.Requested, card.Stage);
        Assert.Equal("Export requested", card.Title);
        Assert.Equal("keynote-cut.mp4 · 4 clips · 4:47 · waiting for your answer", card.Detail);
        Assert.Equal("now", card.WhenText);
        Assert.True(card.IsLive);
        Assert.Equal("Waiting for you", claude.StatusLine);
        Assert.True(claude.IsStatusLive);
        Assert.Equal("MCP · Claude connected", editor.McpText);
        Assert.False(claude.HasNoCards);
        Assert.True(claude.IsOpen);
        Assert.Null(editor.SelectedClip);
        Assert.False(editor.Export.IsDialogOpen);
    }

    [AvaloniaFact]
    public void Allow_runs_the_export_without_the_dialog()
    {
        var editor = App.CreateEditor(DesignScreen.ClaudeRequest);
        var card = editor.Claude.Export;

        card.AllowCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(card.IsAsking);
        Assert.Equal(ClaudeExportStage.Running, card.Stage);
        Assert.Equal("Exporting keynote-cut.mp4", card.Title);
        Assert.True(card.CanCancel);
        Assert.True(card.ShowProgress);
        Assert.False(editor.Export.IsDialogOpen);
        Assert.True(editor.Export.IsByClaude);
        Assert.True(editor.Export.IsInProgress);
        Assert.StartsWith("Exporting ", editor.ExportButtonText, StringComparison.Ordinal);
        Assert.Equal("Exporting", editor.Claude.StatusLine);
    }

    [AvaloniaFact]
    public async Task Deny_tells_Claude_and_leaves_a_card()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        var card = editor.Claude.Export;
        var task = card.AskAsync(DemoScenario.DemoExportTarget(editor));
        Assert.True(editor.Claude.IsOpen);

        card.DenyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ClaudeExportAnswer.Deny, await task);
        Assert.False(card.IsAsking);
        Assert.Equal(ClaudeExportStage.Denied, card.Stage);
        Assert.Equal("Export denied", card.Title);
        Assert.Equal("Claude was told you declined keynote-cut.mp4.", card.Detail);
        Assert.Equal("just now", card.WhenText);
        Assert.True(card.IsEnded);
        Assert.False(editor.Export.IsInProgress);
        Assert.Equal("Idle", editor.Claude.StatusLine);
        Assert.False(editor.Claude.IsStatusLive);
    }

    [AvaloniaFact]
    public async Task Always_allow_answers_AlwaysAllow()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        var card = editor.Claude.Export;
        Assert.Equal(McpPermission.Ask, editor.Settings.ExportPermission);
        var task = card.AskAsync(DemoScenario.DemoExportTarget(editor));

        card.AlwaysAllowCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ClaudeExportAnswer.AlwaysAllow, await task);
        Assert.Equal(McpPermission.Allow, editor.Settings.ExportPermission);
        Assert.False(card.IsAsking);
        // The card waits for the export to start.
        Assert.Equal(ClaudeExportStage.Requested, card.Stage);
    }

    [AvaloniaFact]
    public async Task A_second_request_is_refused_while_one_waits()
    {
        var editor = App.CreateEditor(DesignScreen.ClaudeRequest);
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.Claude.Export.AskAsync(DemoScenario.DemoExportTarget(editor)));
    }

    [AvaloniaFact]
    public async Task A_request_is_withdrawn_when_the_file_closes_or_Claude_cancels()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        var card = editor.Claude.Export;
        var closed = card.AskAsync(DemoScenario.DemoExportTarget(editor));

        editor.Unload();
        Dispatcher.UIThread.RunJobs();

        await Assert.ThrowsAsync<TaskCanceledException>(() => closed);
        Assert.Equal(ClaudeExportStage.None, card.Stage);
        Assert.False(card.IsAsking);
        Assert.False(card.HasCard);

        editor = App.CreateEditor(DesignScreen.Editing);
        card = editor.Claude.Export;
        using var cts = new CancellationTokenSource();
        var cancelled = card.AskAsync(DemoScenario.DemoExportTarget(editor), cts.Token);
        await cts.CancelAsync();
        Dispatcher.UIThread.RunJobs();

        await Assert.ThrowsAsync<TaskCanceledException>(() => cancelled);
        Assert.Equal(ClaudeExportStage.None, card.Stage);
        Assert.False(card.IsAsking);
        Assert.Equal("Idle", editor.Claude.StatusLine);
    }

    [AvaloniaFact]
    public void The_export_card_counts_as_a_card_in_an_empty_log()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        editor.Claude.Log.Clear();
        Assert.True(editor.Claude.HasNoCards);
        var changed = new List<string?>();
        editor.Claude.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _ = editor.Claude.Export.AskAsync(DemoScenario.DemoExportTarget(editor));

        Assert.False(editor.Claude.HasNoCards);
        Assert.Contains(nameof(ClaudePanelViewModel.HasNoCards), changed);
        Assert.Contains(nameof(ClaudePanelViewModel.IsStatusLive), changed);
        Assert.Contains(nameof(ClaudePanelViewModel.Status), changed);
        editor.Claude.Export.Clear();
        Assert.True(editor.Claude.HasNoCards);
    }

    [AvaloniaFact]
    public void Separate_files_are_named_by_their_count()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        editor.Export.Merge = false;
        var target = editor.Export.ClaudeTarget();
        _ = editor.Claude.Export.AskAsync(target);

        Assert.Equal("4 files", target.Name);
        Assert.Equal(@"to C:\Users\You\Videos\Exports · 4 files · Lossless · MP4", editor.Claude.Export.RequestDetail);
        editor.Claude.Export.Clear();
    }

    [AvaloniaFact]
    public void Cancel_on_the_card_stops_the_export()
    {
        var editor = App.CreateEditor(DesignScreen.ClaudeExporting);
        var card = editor.Claude.Export;
        Assert.Equal(ClaudeExportStage.Running, card.Stage);

        card.CancelCommand.Execute(null);

        Assert.Equal(ClaudeExportStage.Cancelled, card.Stage);
        Assert.Equal("Export cancelled", card.Title);
        Assert.Equal("Stopped at 45%. The unfinished file was deleted.", card.Detail);
        Assert.Equal("Export", editor.ExportButtonText);
        Assert.Equal(ExportOutcome.Cancelled, editor.Export.Outcome);
        Assert.False(editor.Export.IsDialogOpen);
        Assert.False(editor.Export.IsInProgress);
    }

    [AvaloniaFact]
    public void A_finished_export_shows_where_it_went()
    {
        var editor = App.CreateEditor(DesignScreen.ClaudeExporting);
        var card = editor.Claude.Export;

        editor.Export.Progress = 1;

        Assert.Equal(ExportOutcome.Done, editor.Export.Outcome);
        Assert.Equal(ClaudeExportStage.Done, card.Stage);
        Assert.Equal("Exported keynote-cut.mp4", card.Title);
        Assert.StartsWith("4 clips · 4:47 · ", card.Detail, StringComparison.Ordinal);
        Assert.EndsWith(@" · D:\Videos", card.Detail, StringComparison.Ordinal);
        Assert.True(card.CanReveal);
        Assert.True(card.IsDone);
        Assert.Equal("just now", card.WhenText);
        Assert.Equal("Export", editor.ExportButtonText);
        Assert.Equal("Idle", editor.Claude.StatusLine);

        // The user's next export leaves the finished card alone.
        editor.Export.Open();
        editor.Export.Start(0.2);
        Assert.Equal("Exported keynote-cut.mp4", card.Title);
        Assert.Equal(ClaudeExportStage.Done, card.Stage);
    }

    [AvaloniaFact]
    public void Retry_restarts_a_failed_export()
    {
        var editor = App.CreateEditor(DesignScreen.ClaudeExportFailed);
        var card = editor.Claude.Export;
        Assert.Equal("Export failed", card.Title);
        Assert.Equal(DemoScenario.DemoExportError, card.Detail);
        Assert.Equal("1 min ago", card.WhenText);
        Assert.True(card.CanRetry);
        Assert.True(card.IsFailed);
        Assert.Equal("Idle", editor.Claude.StatusLine);

        card.RetryCommand.Execute(null);

        Assert.Equal(ClaudeExportStage.Running, card.Stage);
        Assert.Equal(0, editor.Export.Progress);
        Assert.Equal("Exporting keynote-cut.mp4", card.Title);
        Assert.False(editor.Export.IsDialogOpen);
        Assert.True(editor.Export.IsByClaude);
    }

    [AvaloniaFact]
    public void The_running_card_reads_like_the_design()
    {
        var editor = App.CreateEditor(DesignScreen.ClaudeExporting);
        var card = editor.Claude.Export;

        Assert.Equal("Writing clip 3 of 4 · 45% · 5 s left", card.Detail);
        Assert.Equal(0.45, card.Progress, 6);
        Assert.Equal("now", card.WhenText);
        Assert.Equal("Exporting 45%", editor.ExportButtonText);
        Assert.Equal("Exporting", editor.Claude.StatusLine);
        Assert.True(editor.Claude.IsStatusLive);
    }

    [AvaloniaFact]
    public void The_pill_opens_Claudes_running_export_and_the_x_hides_it()
    {
        var editor = App.CreateEditor(DesignScreen.ClaudeExporting);
        var export = editor.Export;
        Assert.False(export.IsDialogOpen);

        export.OpenCommand.Execute(null);
        Assert.True(export.IsDialogOpen);
        Assert.True(export.IsExporting);
        Assert.Equal(0.45, export.Progress, 6);
        Assert.StartsWith("Started by Claude · ", export.ProgressDetail, StringComparison.Ordinal);

        export.DismissCommand.Execute(null);
        Assert.False(export.IsDialogOpen);
        Assert.True(export.IsInProgress);
        Assert.Equal(ClaudeExportStage.Running, editor.Claude.Export.Stage);
    }

    [AvaloniaFact]
    public void Escape_hides_the_users_running_export_instead_of_cancelling_it()
    {
        var editor = App.CreateEditor(DesignScreen.Exporting);
        var export = editor.Export;
        Assert.True(export.IsDialogOpen);
        Assert.DoesNotContain("Started by Claude", export.ProgressDetail, StringComparison.Ordinal);

        Assert.True(Shortcuts.Handle(editor, Key.Escape, KeyModifiers.None));
        Assert.False(export.IsDialogOpen);
        Assert.True(export.IsInProgress);
        Assert.StartsWith("Exporting ", editor.ExportButtonText, StringComparison.Ordinal);
        // A user export never shows a Claude card.
        Assert.False(editor.Claude.Export.HasCard);

        Assert.True(Shortcuts.Handle(editor, Key.E, KeyModifiers.Control));
        Assert.True(export.IsDialogOpen);
        Assert.True(export.IsExporting);

        export.DismissCommand.Execute(null);
        Assert.True(export.IsInProgress);
        export.CancelExportCommand.Execute(null);
        Assert.False(export.IsInProgress);
        Assert.False(export.IsDialogOpen);
    }

    [AvaloniaFact]
    public void The_mcp_badge_names_each_state()
    {
        var claude = App.CreateEditor(null).Claude;
        Assert.Equal("MCP · Off", claude.McpText);
        Assert.False(claude.IsMcpOn);

        claude.IsServedElsewhere = true;
        Assert.Equal("MCP · In another window", claude.McpText);
        Assert.False(claude.IsMcpOn);

        claude.IsListening = true;
        Assert.Equal("MCP · Waiting for Claude", claude.McpText);
        Assert.Equal(McpStatus.Waiting, claude.Status);
        Assert.False(claude.IsMcpOn);

        claude.IsConnected = true;
        Assert.Equal("MCP · Claude connected", claude.McpText);
        Assert.Equal(McpStatus.Connected, claude.Status);
        Assert.True(claude.IsMcpOn);

        claude.NoteActivity();
        Assert.Equal("MCP · Claude editing", claude.McpText);
        Assert.Equal(McpStatus.Editing, claude.Status);
        Assert.True(claude.IsMcpOn);
    }

    [AvaloniaFact]
    public void The_badge_follows_every_status_change()
    {
        var claude = App.CreateEditor(null).Claude;
        var changed = new List<string?>();
        claude.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        claude.IsListening = true;

        Assert.Contains(nameof(ClaudePanelViewModel.McpText), changed);
        Assert.Contains(nameof(ClaudePanelViewModel.IsMcpOn), changed);
    }

    [AvaloniaFact]
    public void Claudes_export_does_not_count_as_editing()
    {
        var editor = App.CreateEditor(DesignScreen.ClaudeExporting);
        editor.Claude.NoteActivity();

        Assert.Equal("MCP · Claude connected", editor.McpText);
        Assert.Equal(McpStatus.Connected, editor.Claude.Status);
        Assert.Equal("Exporting", editor.Claude.StatusLine);
        Assert.True(editor.Claude.IsStatusLive);
        Assert.False(editor.Claude.IsWorking);
    }

    [AvaloniaFact]
    public void Opening_another_file_drops_the_card()
    {
        var editor = App.CreateEditor(DesignScreen.ClaudeExportFailed);
        Assert.True(editor.Claude.Export.HasCard);

        editor.Unload();
        Assert.False(editor.Claude.Export.HasCard);

        editor = App.CreateEditor(DesignScreen.ClaudeExportFailed);
        DemoScenario.OpenSample(editor);
        Assert.False(editor.Claude.Export.HasCard);
        Assert.Equal(ClaudeExportStage.None, editor.Claude.Export.Stage);
    }

    [AvaloniaFact]
    public async Task The_sample_cannot_be_exported_through_MCP()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        var host = new EditorMcpHost(editor);

        string? error = await host.StartExportAsync(new HighlightCut.Mcp.ExportRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("Exporting is not available for this file.", error);
        Assert.False(editor.Export.IsDialogOpen);
        Assert.False(editor.Claude.Export.HasCard);
        Assert.False(host.CancelExport());
    }
}
