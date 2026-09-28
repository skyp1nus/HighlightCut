using System.IO.Pipes;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Tests;

/// <summary>Claude editing the open project through the editor's MCP server, and how the editor shows it.</summary>
public sealed class McpIntegrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-mcp-app").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task PumpUntil(Func<bool> done)
    {
        for (int i = 0; i < 250 && !done(); i++)
        {
            await Task.Delay(20, Ct);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(done());
    }

    private static async Task<EditorViewModel> EditorWithVideoAsync()
    {
        var editor = App.CreateEditor(null, new SampleOpener());
        await editor.OpenMediaAsync("/videos/talk.mp4");
        Assert.True(editor.HasFile);
        return editor;
    }

    [AvaloniaFact]
    public async Task Claudes_edits_show_up_highlighted_and_the_user_can_undo_them()
    {
        var editor = await EditorWithVideoAsync();
        await using var server = new EditorMcpServer(editor, "highlightcut-app-test-" + Guid.NewGuid().ToString("N")[..12]);
        server.Start();
        await PumpUntil(() => editor.Claude.IsListening);
        Assert.Equal("MCP · Waiting for Claude", editor.McpText);

        var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000, Ct);
        var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: Ct);
        await PumpUntil(() => editor.Claude.IsConnected);

        var result = await client.CallToolAsync("add_segment",
            new Dictionary<string, object?> { ["start"] = 10.0, ["end"] = 20.0, ["label"] = "Intro" }, cancellationToken: Ct);
        Assert.True(result.IsError is not true);
        var clip = Assert.Single(editor.Clips);
        Assert.Equal(("Intro", 10.0, 20.0), (clip.Label, clip.Start, clip.End));
        Assert.True(clip.IsAiChanged);
        var card = Assert.Single(editor.Claude.Log);
        Assert.True(card.IsHighlighted);
        Assert.Equal("MCP · Claude editing", editor.McpText);

        // Claude sees what the user does.
        editor.SetTime(12);
        var project = await client.CallToolAsync("get_project", cancellationToken: Ct);
        Assert.Contains("\"playhead\":12", project.Content.OfType<TextContentBlock>().Single().Text,
            StringComparison.Ordinal);

        // Undo on Claude's card reverts Claude's edit.
        card.Undo();
        Assert.Empty(editor.Clips);
        Assert.False(card.IsHighlighted);

        await client.DisposeAsync();
        await pipe.DisposeAsync();
        await PumpUntil(() => !editor.Claude.IsConnected);
        Assert.Equal("MCP · Waiting for Claude", editor.McpText);
    }

    [AvaloniaFact]
    public void The_badge_says_whether_Claude_can_connect()
    {
        var claude = App.CreateEditor(null).Claude;
        Assert.Equal("MCP · Off", claude.McpText);
        claude.IsServedElsewhere = true;
        Assert.Equal("MCP · In another window", claude.McpText);
        claude.IsServedElsewhere = false;
        claude.IsListening = true;
        Assert.Equal("MCP · Waiting for Claude", claude.McpText);
        claude.IsConnected = true;
        Assert.Equal("MCP · Claude connected", claude.McpText);
        claude.NoteActivity();
        Assert.Equal("MCP · Claude editing", claude.McpText);
        Assert.Equal("Editing timeline", claude.StatusLine);
    }

    [AvaloniaFact]
    public async Task Opening_a_file_keeps_Claude_connected()
    {
        var editor = App.CreateEditor(null, new SampleOpener());
        editor.Claude.IsListening = true;
        editor.Claude.IsConnected = true;
        await editor.OpenMediaAsync("/videos/talk.mp4");
        await editor.OpenMediaAsync("/videos/other.mp4");
        Assert.True(editor.Claude.IsConnected);
    }

    [AvaloniaFact]
    public async Task Claude_gets_the_reason_when_a_file_does_not_open_or_save()
    {
        var editor = App.CreateEditor(null, new SampleOpener());
        Assert.Equal("No video is open.", await editor.SaveForClaudeAsync(null));
        Assert.Contains("was not found", await editor.OpenForClaudeAsync("/videos/missing.mp4"), StringComparison.Ordinal);
        Assert.Null(await editor.OpenForClaudeAsync("/videos/talk.mp4"));

        Assert.Contains("has not been saved", await editor.SaveForClaudeAsync(null), StringComparison.Ordinal);
        Assert.Contains(".highlightcut.json", await editor.SaveForClaudeAsync(Path.Combine(_dir, "talk.txt")), StringComparison.Ordinal);
        string path = Path.Combine(_dir, "talk.highlightcut.json");
        Assert.Null(await editor.SaveForClaudeAsync(path));
        Assert.Equal(path, editor.ProjectPath);
        Assert.True(File.Exists(path));
        Assert.Null(await editor.SaveForClaudeAsync(null));
    }

    [AvaloniaFact]
    public async Task Settings_show_how_to_add_HighlightCut_to_Claude()
    {
        var settings = App.CreateEditor(null).Settings;
        Assert.True(settings.IsTranscription);
        var mcp = settings.SectionOptions.Single(o => o.Label == "MCP server");
        mcp.PickCommand.Execute(null);
        Assert.True(settings.IsMcp);
        Assert.False(settings.IsTranscription);
        Assert.True(mcp.IsSelected);
        Assert.False(settings.SectionOptions.Single(o => o.Label == "Transcription").IsSelected);

        Assert.StartsWith("claude mcp add --scope user highlightcut -- ", settings.ClaudeCodeCommand, StringComparison.Ordinal);
        Assert.EndsWith(" mcp", settings.ClaudeCodeCommand, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(settings.ClaudeDesktopConfig);
        var entry = json.RootElement.GetProperty("mcpServers").GetProperty("highlightcut");
        Assert.Equal(settings.McpCommand, entry.GetProperty("command").GetString());
        Assert.Equal(settings.McpArgs, entry.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("mcp", settings.McpArgs[^1]);

        string? copied = null;
        settings.CopyText = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };
        await settings.CopyClaudeCodeCommand.ExecuteAsync(null);
        Assert.Equal(settings.ClaudeCodeCommand, copied);
        await settings.CopyClaudeDesktopCommand.ExecuteAsync(null);
        Assert.Equal(settings.ClaudeDesktopConfig, copied);

        settings.SectionOptions.Single(o => o.Label == "Keyboard").PickCommand.Execute(null);
        Assert.True(settings.IsKeyboard);
    }
}
