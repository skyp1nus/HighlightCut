using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;
using HighlightCut.Mcp;

namespace HighlightCut.App.Tests;

/// <summary>Settings → MCP server → Permissions: what Claude may open, save and export without asking.</summary>
public sealed class ClaudePermissionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-permissions").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(EditorViewModel Editor, EditorMcpHost Host)> OpenAsync()
    {
        var editor = App.CreateEditor(null, new SampleOpener());
        await editor.OpenMediaAsync("/videos/talk.mp4");
        Assert.True(editor.HasFile);
        return (editor, new EditorMcpHost(editor));
    }

    [AvaloniaFact]
    public async Task Opening_asks_first_when_the_setting_says_Ask()
    {
        var (editor, host) = await OpenAsync();
        editor.Settings.OpenFilesPermission = McpPermission.Ask;
        var files = editor.Claude.Files;

        var denied = host.OpenAsync("/videos/other.mp4", Ct);

        Assert.True(files.IsAsking);
        Assert.True(editor.Claude.IsAsking);
        Assert.Same(files, editor.Claude.Request);
        Assert.Equal("Claude wants to open other.mp4", files.RequestTitle);
        Assert.Equal("/videos/other.mp4 · closes talk", files.RequestDetail);
        // Claude's call is still running, but the panel says whose turn it is.
        editor.Claude.NoteActivity();
        Assert.Equal("Waiting for you", editor.Claude.StatusLine);
        Assert.True(editor.Claude.IsStatusLive);
        Assert.False(editor.Claude.IsWorking);
        files.DenyCommand.Execute(null);
        Assert.Equal("The user declined opening other.mp4.", await denied);
        Assert.Equal("talk.mp4", editor.MediaFileName);
        Assert.Same(editor.Claude.Export, editor.Claude.Request);

        var allowed = host.OpenAsync("/videos/other.mp4", Ct);
        files.AllowCommand.Execute(null);
        Assert.Null(await allowed);
        Assert.Equal("other.mp4", editor.MediaFileName);
        Assert.Equal(McpPermission.Ask, editor.Settings.OpenFilesPermission);
    }

    [AvaloniaFact]
    public async Task Opening_is_allowed_by_default_and_Always_allow_stops_the_asking()
    {
        var (editor, host) = await OpenAsync();
        Assert.Equal(McpPermission.Allow, editor.Settings.OpenFilesPermission);
        Assert.Null(await host.OpenAsync("/videos/other.mp4", Ct));
        Assert.Equal("other.mp4", editor.MediaFileName);

        editor.Settings.OpenFilesPermission = McpPermission.Ask;
        var task = host.OpenAsync("/videos/third.mp4", Ct);
        editor.Claude.Files.AlwaysAllowCommand.Execute(null);
        Assert.Null(await task);
        Assert.Equal(McpPermission.Allow, editor.Settings.OpenFilesPermission);

        Assert.Null(await host.OpenAsync("/videos/fourth.mp4", Ct));
        Assert.False(editor.Claude.Files.IsAsking);
        Assert.Equal("fourth.mp4", editor.MediaFileName);
    }

    [AvaloniaFact]
    public async Task Saving_asks_by_default_and_says_when_it_replaces_a_file()
    {
        var (editor, host) = await OpenAsync();
        Assert.Equal(McpPermission.Ask, editor.Settings.SaveProjectPermission);
        string path = Path.Combine(_dir, "talk.highlightcut.json");
        await File.WriteAllTextAsync(path, "{}", Ct);
        var files = editor.Claude.Files;

        var save = host.SaveAsync(path, Ct);
        Assert.Equal("Claude wants to save talk", files.RequestTitle);
        Assert.Equal($"to {path} · replaces that file", files.RequestDetail);
        files.AllowCommand.Execute(null);
        Assert.Null(await save);
        Assert.Equal(path, editor.ProjectPath);

        // Where the project was saved before: nothing else is replaced.
        var again = host.SaveAsync(null, Ct);
        Assert.Equal($"to {path}", files.RequestDetail);
        files.DenyCommand.Execute(null);
        Assert.Equal("The user declined saving the project.", await again);
    }

    [AvaloniaFact]
    public async Task A_save_that_cannot_happen_is_refused_without_asking()
    {
        var (editor, host) = await OpenAsync();

        Assert.Contains("has not been saved", await host.SaveAsync(null, Ct), StringComparison.Ordinal);
        Assert.Contains(".highlightcut.json", await host.SaveAsync(Path.Combine(_dir, "talk.txt"), Ct), StringComparison.Ordinal);
        Assert.False(editor.Claude.Files.IsAsking);
    }

    [AvaloniaFact]
    public async Task Claude_asks_one_thing_at_a_time_and_can_withdraw_a_request()
    {
        var (editor, host) = await OpenAsync();
        string path = Path.Combine(_dir, "talk.highlightcut.json");
        using var cts = new CancellationTokenSource();
        var save = host.SaveAsync(path, cts.Token);

        Assert.StartsWith("Another request is waiting", await host.SaveAsync(path, Ct), StringComparison.Ordinal);
        Assert.StartsWith("Another request is waiting", await host.StartExportAsync(new ExportRequest(), Ct), StringComparison.Ordinal);

        await cts.CancelAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("The request was withdrawn.", await save);
        Assert.False(editor.Claude.IsAsking);
        Assert.False(File.Exists(path));
    }

    [AvaloniaFact]
    public async Task Opening_another_file_withdraws_a_save_request()
    {
        var (editor, host) = await OpenAsync();
        string path = Path.Combine(_dir, "talk.highlightcut.json");
        var save = host.SaveAsync(path, Ct);

        await editor.OpenMediaAsync("/videos/other.mp4");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("The request was withdrawn.", await save);
        Assert.False(File.Exists(path));
    }

    [AvaloniaFact]
    public async Task Never_leaves_exporting_to_the_user()
    {
        var (editor, host) = await OpenAsync();
        editor.Settings.ExportPermission = McpPermission.Never;

        string? error = await host.StartExportAsync(new ExportRequest(), Ct);

        Assert.Contains("Export: Never", error, StringComparison.Ordinal);
        Assert.False(editor.Claude.Export.HasCard);
        Assert.False(editor.Export.IsInProgress);
    }

    [AvaloniaFact]
    public async Task The_banner_shows_an_open_request()
    {
        var (editor, host) = await OpenAsync();
        editor.Settings.OpenFilesPermission = McpPermission.Ask;
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        var banner = window.GetVisualDescendants().OfType<ClaudeRequestBanner>().Single();
        var box = banner.GetVisualDescendants().OfType<Border>().First();
        Assert.False(box.IsVisible);

        var open = host.OpenAsync("/videos/other.mp4", Ct);
        Dispatcher.UIThread.RunJobs();

        Assert.True(box.IsVisible);
        Assert.Contains(banner.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Claude wants to open other.mp4");
        banner.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Deny")).Command!.Execute(null);
        Assert.NotNull(await open);
        Dispatcher.UIThread.RunJobs();
        Assert.False(box.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Claude_finds_the_users_filler_words()
    {
        var editor = App.CreateEditor(null);
        editor.Settings.SetFillerWords("en", ["um", "basically"]);

        var fillers = new EditorMcpHost(editor).FillerWords;

        Assert.Contains("basically", fillers);
        Assert.Contains("ну", fillers);
        Assert.DoesNotContain("uh", fillers);
    }
}
