using System.IO.Pipes;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OurCut.Core.Editing;
using OurCut.Core.Model;
using OurCut.Core.Transcripts;

namespace OurCut.Mcp.Tests;

/// <summary>An editor without UI: a real <see cref="EditorSession"/> plus the view state the tools touch.</summary>
internal sealed class FakeEditor : IEditorHost, IEditorContext, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static Project Sample { get; } = new("keynote",
        new SourceMedia("/videos/keynote.mp4", 600, 30, [new AudioTrack(1, "Mic"), new AudioTrack(2, "Music")]),
        [new Clip(1, "Intro", 10, 40), new Clip(2, "Demo", 100, 200), new Clip(3, "Q&A", 300, 360, IsIncluded: false)]);

    public EditorSession Session { get; } = new();
    public bool HasFile { get; set; }
    public string? SourceSummary => HasFile ? "keynote.mp4 · 1080p · 30 fps" : null;
    public string? ProjectPath { get; set; }
    public double Playhead { get; set; }
    public int? SelectedClipId { get; set; }
    public bool IsPlaying { get; set; }
    public IReadOnlyList<double> Keyframes { get; set; } = [0, 2, 4, 9.5, 10, 12, 99, 100, 102];
    public SilenceReport? Silences { get; set; } = new([new(50, 52.5), new(120, 124), new(150, 150.8), new(330, 340)], -48, -60, true);
    public SceneReport? Scenes { get; set; } = new([20, 105.5, 180, 420], true, 1);
    public List<(double MinDuration, double? ThresholdDb, IReadOnlyList<int>? Streams)> SilenceQueries { get; } = [];
    public string? AnalysisStatus { get; set; }
    public List<string> Opened { get; } = [];
    public int Calls { get; private set; }

    public FakeEditor(bool withFile = true)
    {
        if (withFile)
            Open(Sample);
    }

    public void Open(Project project)
    {
        Session.Load(project);
        HasFile = true;
    }

    public async Task<T> RunAsync<T>(Func<IEditorContext, Task<T>> action)
    {
        await _gate.WaitAsync();
        try
        {
            Calls++;
            return await action(this);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Filters <see cref="Silences"/> by length, like the real detector.</summary>
    public SilenceReport? FindSilences(double minDuration, double? thresholdDb, IReadOnlyList<int>? streams)
    {
        SilenceQueries.Add((minDuration, thresholdDb, streams));
        return Silences is { } s ? s with { Ranges = [.. s.Ranges.Where(r => r.Duration >= minDuration)], ThresholdDb = thresholdDb ?? s.ThresholdDb } : null;
    }

    public SceneReport? FindSceneChanges(double threshold) =>
        Scenes is { } s ? s with { Times = threshold > 20 ? [.. s.Times.Take(1)] : s.Times } : null;

    public TranscriptStatus TranscriptStatus { get; set; } = new("done", 1, new Transcript("parakeet-tdt-0.6b-v3", "auto",
    [
        new("Welcome", 10.0, 10.4), new("to", 10.5, 10.6), new("the", 10.7, 10.8), new("keynote.", 10.9, 11.4),
        new("Um,", 12.0, 12.3), new("so", 12.5, 12.7), new("today", 12.8, 13.1), new("we", 13.2, 13.3), new("export", 13.4, 13.8),
        new("videos.", 13.9, 14.4), new("Uh", 105.0, 105.3), new("the", 105.5, 105.6), new("export", 105.7, 106.1), new("dialog.", 106.2, 106.7),
    ]), null);

    /// <summary>The user's filler words (Settings → Transcription).</summary>
    public IReadOnlyList<string> FillerWords { get; set; } = OurCut.Core.Transcripts.FillerWords.All(OurCut.Core.Transcripts.FillerWords.Defaults);

    /// <summary>Why transcription cannot start (null: it starts and is done at once).</summary>
    public string? TranscriptionRefusal { get; set; }

    public int TranscriptionStarts { get; private set; }

    public string? StartTranscription()
    {
        TranscriptionStarts++;
        if (TranscriptionRefusal is { } refusal)
            return refusal;
        TranscriptStatus = TranscriptStatus with { State = "waiting" };
        return null;
    }

    /// <summary>Why an export cannot start (null: it starts).</summary>
    public string? ExportRefusal { get; set; }

    /// <summary>How many times the export is read as running before it is done.</summary>
    public int ExportReads { get; set; } = 1;

    public ExportRequest? LastExport { get; private set; }
    private ExportState? _export;
    private int _exportReadsLeft;

    public Task<string?> StartExportAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        if (ExportRefusal is { } refusal)
            return Task.FromResult<string?>(refusal);
        LastExport = request;
        _export = new ExportState("running", 0.1, ["/videos/keynote-cut.mp4"], null, "Lossless copy · MP4 · merged");
        _exportReadsLeft = ExportReads;
        return Task.FromResult<string?>(null);
    }

    public ExportState? Export
    {
        get
        {
            if (_export is { Status: "running" } running && _exportReadsLeft-- <= 0)
                _export = running with { Status = "done", Progress = 1 };
            return _export;
        }
    }

    public bool CancelExport()
    {
        if (_export is not { Status: "running" } running)
            return false;
        _export = running with { Status = "cancelled" };
        return true;
    }

    public void Seek(double time) => Playhead = time;
    public void SelectClip(int? clipId) => SelectedClipId = clipId;
    public void SetPlaying(bool playing) => IsPlaying = playing;

    public Task<string?> OpenAsync(string path, CancellationToken cancellationToken)
    {
        Opened.Add(path);
        if (path.EndsWith(".broken", StringComparison.Ordinal))
            return Task.FromResult<string?>("ffprobe could not read the file.");
        Open(Sample with { Name = Path.GetFileNameWithoutExtension(path), Clips = [] });
        return Task.FromResult<string?>(null);
    }

    public Task<string?> SaveAsync(string? path, CancellationToken cancellationToken)
    {
        ProjectPath = path ?? ProjectPath;
        return Task.FromResult<string?>(null);
    }
}

/// <summary>Starts the editor's pipe server and connects an MCP client to it, like the bridge does.</summary>
internal sealed class Connection : IAsyncDisposable
{
    private readonly McpPipeServer _server;
    private readonly NamedPipeClientStream _pipe;

    private Connection(McpPipeServer server, NamedPipeClientStream pipe, McpClient client)
    {
        _server = server;
        _pipe = pipe;
        Client = client;
    }

    public McpClient Client { get; }

    public static string NewPipeName() => "ourcut-test-" + Guid.NewGuid().ToString("N")[..12];

    public static async Task<Connection> OpenAsync(IEditorHost host)
    {
        var server = new McpPipeServer(host, NewPipeName());
        // Connect once the pipe is there: ConnectAsync blocks a thread-pool thread while it waits, and on a busy runner
        // that can starve the server's own start of the thread it needs.
        await server.Start().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(30_000, TestContext.Current.CancellationToken);
        var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: TestContext.Current.CancellationToken);
        return new Connection(server, pipe, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _pipe.DisposeAsync();
        await _server.DisposeAsync();
    }
}

internal static class Calls
{
    public static async Task<CallToolResult> Call(this McpClient client, string tool, object? args = null)
    {
        var dict = args is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        return await client.CallToolAsync(tool, dict, cancellationToken: TestContext.Current.CancellationToken);
    }

    public static string Text(this CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    public static JsonElement Json(this CallToolResult result)
    {
        Assert.True(result.IsError is not true, result.Text());
        return JsonDocument.Parse(result.Text()).RootElement;
    }
}

public class McpToolListTests
{
    private static readonly string[] Expected =
    [
        "add_segment", "cancel_export", "cut_filler_words", "cut_ranges", "cut_silences", "edit_timeline", "export",
        "find_filler_words", "find_keyframes", "find_scene_changes", "find_silences", "get_export_status", "get_history", "get_project",
        "get_transcript", "list_videos", "move_segment", "open_file", "redo", "remove_segment", "revert_action", "save_project",
        "search_transcript", "seek", "set_included", "set_label", "set_playing", "split_segment", "trim_segment", "undo",
    ];

    [Fact]
    public async Task The_editor_serves_every_tool_with_annotations()
    {
        await using var c = await Connection.OpenAsync(new FakeEditor());
        var tools = await c.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Expected, tools.Select(t => t.Name).Order());
        var getProject = tools.Single(t => t.Name == "get_project").ProtocolTool;
        Assert.True(getProject.Annotations?.ReadOnlyHint);
        var remove = tools.Single(t => t.Name == "remove_segment").ProtocolTool;
        Assert.True(remove.Annotations?.DestructiveHint);
        var add = tools.Single(t => t.Name == "add_segment").ProtocolTool;
        Assert.Equal(["end", "start"], add.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.Contains("OurCut", c.Client.ServerInstructions, StringComparison.Ordinal);
    }
}

public class McpEditingTests
{
    [Fact]
    public async Task Get_project_lists_the_source_and_clips_in_output_order()
    {
        var editor = new FakeEditor { Playhead = 12.5, SelectedClipId = 2 };
        await using var c = await Connection.OpenAsync(editor);

        var p = (await c.Client.Call("get_project")).Json();

        Assert.Equal("keynote", p.GetProperty("name").GetString());
        Assert.Equal(600, p.GetProperty("source").GetProperty("duration").GetDouble());
        Assert.Equal(["Mic", "Music"], p.GetProperty("source").GetProperty("audioTracks").EnumerateArray().Select(t => t.GetProperty("label").GetString()));
        Assert.Equal(130, p.GetProperty("outputDuration").GetDouble());
        Assert.Equal(12.5, p.GetProperty("playhead").GetDouble());
        Assert.Equal(2, p.GetProperty("selectedClip").GetInt32());
        var clips = p.GetProperty("clips").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], clips.Select(x => x.GetProperty("id").GetInt32()));
        Assert.False(clips[2].GetProperty("included").GetBoolean());
        Assert.Equal("00:10.000–00:40.000", clips[0].GetProperty("range").GetString());
    }

    [Fact]
    public async Task Edits_are_made_as_claude_and_can_be_undone_by_id()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);

        var added = (await c.Client.Call("add_segment", new { start = 400, end = 420.5, label = "Outro", position = 1 })).Json();
        Assert.Equal(4, added.GetProperty("clips")[0].GetProperty("id").GetInt32());
        Assert.Equal(150.5, added.GetProperty("outputDuration").GetDouble());
        long action = added.GetProperty("action").GetInt64();

        var entry = Assert.Single(editor.Session.History.Entries);
        Assert.Equal(EditOrigin.Assistant, entry.Origin);
        Assert.Equal(action, entry.Id);

        (await c.Client.Call("trim_segment", new { clip = 2, end = 150 })).Json();
        Assert.Equal((100.0, 150.0), (editor.Session.Project.Get(2).Start, editor.Session.Project.Get(2).End));

        var reverted = (await c.Client.Call("revert_action", new { action })).Json();
        Assert.StartsWith("Reverted", reverted.GetProperty("result").GetString(), StringComparison.Ordinal);
        Assert.Null(editor.Session.Project.Find(4));
        Assert.Equal(150, editor.Session.Project.Get(2).End);
    }

    [Fact]
    public async Task Invalid_edits_come_back_as_tool_errors_claude_can_read()
    {
        await using var c = await Connection.OpenAsync(new FakeEditor());

        var missing = await c.Client.Call("remove_segment", new { clip = 99 });
        Assert.True(missing.IsError);
        Assert.Contains("Clip 99 does not exist", missing.Text(), StringComparison.Ordinal);

        var backwards = await c.Client.Call("add_segment", new { start = 50, end = 20 });
        Assert.True(backwards.IsError);
    }

    [Fact]
    public async Task Edit_timeline_is_one_undo_step_with_claudes_description()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);

        var result = (await c.Client.Call("edit_timeline", new
        {
            description = "Kept the two demos",
            operations = new object[]
            {
                new { action = "add", start = 220, end = 250, label = "Demo 2" },
                new { action = "exclude", clip = 1 },
                new { action = "rename", clip = 2, label = "Demo 1" },
                new { action = "trim", clip = 2, start = 99 },
            },
        })).Json();

        var entry = Assert.Single(editor.Session.History.Entries);
        Assert.Equal("Kept the two demos", entry.Description);
        Assert.Equal(EditOrigin.Assistant, entry.Origin);
        var p = editor.Session.Project;
        Assert.False(p.Get(1).IsIncluded);
        Assert.Equal(("Demo 1", 99.0), (p.Get(2).Label, p.Get(2).Start));
        Assert.Equal("Demo 2", p.Get(4).Label);
        Assert.Equal(131, result.GetProperty("outputDuration").GetDouble());
    }

    [Fact]
    public async Task A_failing_batch_changes_nothing()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);

        var result = await c.Client.Call("edit_timeline", new
        {
            description = "Half done",
            operations = new object[] { new { action = "exclude", clip = 1 }, new { action = "explode", clip = 2 } },
        });

        Assert.True(result.IsError);
        Assert.Contains("explode", result.Text(), StringComparison.Ordinal);
        Assert.Empty(editor.Session.History.Entries);
        Assert.True(editor.Session.Project.Get(1).IsIncluded);
    }

    [Fact]
    public async Task Undo_redo_and_history()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);
        editor.Session.Execute(new OurCut.Core.Editing.Commands.RenameClipCommand(1, "Opening"));
        (await c.Client.Call("set_included", new { clip = 2, included = false })).Json();

        var history = (await c.Client.Call("get_history")).Json().EnumerateArray().ToList();
        Assert.Equal(["claude", "user"], history.Select(h => h.GetProperty("by").GetString()));

        Assert.Contains("Undid", (await c.Client.Call("undo")).Json().GetProperty("result").GetString(), StringComparison.Ordinal);
        Assert.True(editor.Session.Project.Get(2).IsIncluded);
        (await c.Client.Call("redo")).Json();
        Assert.False(editor.Session.Project.Get(2).IsIncluded);
        Assert.True((await c.Client.Call("redo")).IsError);
    }

    [Fact]
    public async Task Seek_can_select_a_clip_and_go_to_its_start()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);

        Assert.Contains("00:01:40.000", (await c.Client.Call("seek", new { clip = 2 })).Text(), StringComparison.Ordinal);
        Assert.Equal((100.0, 2), (editor.Playhead, editor.SelectedClipId));
        (await c.Client.Call("seek", new { time = 1000 })).Text();
        Assert.Equal(600, editor.Playhead);
        (await c.Client.Call("set_playing", new { playing = true })).Text();
        Assert.True(editor.IsPlaying);
    }

    [Fact]
    public async Task Keyframes_are_listed_for_a_range()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);
        Assert.Equal([9.5, 10, 12], (await c.Client.Call("find_keyframes", new { start = 5, end = 20 })).Json().EnumerateArray()
            .Select(e => e.GetDouble()));

        editor.Keyframes = [];
        editor.AnalysisStatus = "analysing 40%";
        var pending = await c.Client.Call("find_keyframes", new { start = 0, end = 10 });
        Assert.True(pending.IsError);
        Assert.Contains("analysing 40%", pending.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_video_edits_explain_how_to_open_one()
    {
        var editor = new FakeEditor(withFile: false);
        await using var c = await Connection.OpenAsync(editor);

        Assert.Equal(JsonValueKind.Null, (await c.Client.Call("get_project")).Json().TryGetProperty("source", out var s) ? s.ValueKind : JsonValueKind.Null);
        var result = await c.Client.Call("add_segment", new { start = 1, end = 2 });
        Assert.True(result.IsError);
        Assert.Contains("open_file", result.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_file_opens_videos_and_reports_failures()
    {
        string dir = Directory.CreateTempSubdirectory("ourcut-mcp").FullName;
        try
        {
            string video = Path.Combine(dir, "talk.mp4"), broken = Path.Combine(dir, "bad.broken");
            await File.WriteAllTextAsync(video, "", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(broken, "", TestContext.Current.CancellationToken);
            var editor = new FakeEditor(withFile: false);
            await using var c = await Connection.OpenAsync(editor);

            Assert.Equal("talk", (await c.Client.Call("open_file", new { path = video })).Json().GetProperty("name").GetString());
            var failed = await c.Client.Call("open_file", new { path = broken });
            Assert.True(failed.IsError);
            Assert.Contains("ffprobe", failed.Text(), StringComparison.Ordinal);
            Assert.True((await c.Client.Call("open_file", new { path = Path.Combine(dir, "missing.mp4") })).IsError);
            Assert.Contains("full path", (await c.Client.Call("open_file", new { path = "talk.mp4" })).Text(), StringComparison.Ordinal);
            Assert.Equal([video, broken], editor.Opened);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task List_videos_finds_the_newest_first()
    {
        string dir = Directory.CreateTempSubdirectory("ourcut-videos").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "2026"));
            string old = Path.Combine(dir, "old.mov"), recent = Path.Combine(dir, "2026", "new.MP4");
            await File.WriteAllTextAsync(old, "x", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(recent, "xy", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(dir, "notes.txt"), "", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
            await using var c = await Connection.OpenAsync(new FakeEditor());

            var files = (await c.Client.Call("list_videos", new { folder = dir })).Json().EnumerateArray().ToList();

            Assert.Equal([recent, old], files.Select(f => f.GetProperty("path").GetString()));
            Assert.Equal(2, files[0].GetProperty("sizeBytes").GetInt64());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Save_needs_a_full_path_the_first_time()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);
        string path = Path.Combine(Path.GetTempPath(), "a.ourcut.json");
        Assert.True((await c.Client.Call("save_project")).IsError);
        Assert.True((await c.Client.Call("save_project", new { path = "a.ourcut.json" })).IsError);
        Assert.Contains(path, (await c.Client.Call("save_project", new { path })).Text(), StringComparison.Ordinal);
        Assert.Contains(path, (await c.Client.Call("save_project")).Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_server_counts_its_sessions()
    {
        var server = new McpPipeServer(new FakeEditor(), Connection.NewPipeName());
        await using (server)
        {
            await server.Start().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            int changes = 0;
            server.StateChanged += (_, _) => Interlocked.Increment(ref changes);
            var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(30_000, TestContext.Current.CancellationToken);
            await WaitUntil(() => server.Sessions == 1);
            await pipe.DisposeAsync();
            await WaitUntil(() => server.Sessions == 0);
            Assert.Equal(2, changes);
        }
        Assert.False(server.IsListening);
    }

    [Fact]
    public async Task A_second_editor_waits_until_the_first_one_closes()
    {
        string name = Connection.NewPipeName();
        var first = new McpPipeServer(new FakeEditor(), name);
        await first.Start().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await using var second = new McpPipeServer(new FakeEditor(), name) { RetryDelay = TimeSpan.FromMilliseconds(50) };
        _ = second.Start();
        await WaitUntil(() => second.IsInUseElsewhere);
        Assert.False(second.IsListening);

        await first.DisposeAsync();
        await WaitUntil(() => second.IsListening);
        Assert.False(second.IsInUseElsewhere);
    }

    [Fact]
    public async Task A_waiting_editor_knows_which_project_has_the_pipe()
    {
        string name = Connection.NewPipeName();
        var first = new McpPipeServer(new FakeEditor(), name) { OwnerLabel = "keynote.mp4" };
        await first.Start().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await using var second = new McpPipeServer(new FakeEditor(), name) { RetryDelay = TimeSpan.FromMilliseconds(50), OwnerLabel = "talk.mp4" };
        int changes = 0;
        second.StateChanged += (_, _) => Interlocked.Increment(ref changes);
        _ = second.Start();
        await WaitUntil(() => second.OtherOwnerLabel == "keynote.mp4");
        Assert.True(second.IsInUseElsewhere);

        // The first window saves its project: the waiting one hears about it.
        int before = Volatile.Read(ref changes);
        first.OwnerLabel = "keynote.ourcut.json";
        await WaitUntil(() => second.OtherOwnerLabel == "keynote.ourcut.json");
        Assert.True(Volatile.Read(ref changes) > before);

        await first.DisposeAsync();
        await WaitUntil(() => second.IsListening);
        Assert.Null(second.OtherOwnerLabel);
        Assert.Equal("talk.mp4", (await File.ReadAllTextAsync(second.OwnerPath, TestContext.Current.CancellationToken)).Trim());
        await second.DisposeAsync();
        Assert.False(File.Exists(second.OwnerPath));
    }

    [Fact]
    public async Task The_server_knows_which_client_is_connected()
    {
        var editor = new FakeEditor();
        await using var server = new McpPipeServer(editor, Connection.NewPipeName());
        await server.Start().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await using var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(30_000, TestContext.Current.CancellationToken);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe),
            new McpClientOptions { ClientInfo = new Implementation { Name = "claude-code", Version = "2.1.0" } },
            cancellationToken: TestContext.Current.CancellationToken);

        await WaitUntil(() => server.ClientName == "Claude Code");
    }

    [Theory]
    [InlineData("claude-ai", null, "Claude Desktop")]
    [InlineData("claude-code", null, "Claude Code")]
    [InlineData("cursor-vscode", null, "cursor-vscode")]
    [InlineData("some-client", "Some Client", "Some Client")]
    [InlineData("ourcut-bridge", null, null)]
    [InlineData("", null, null)]
    public void Clients_are_named_as_the_user_knows_them(string name, string? title, string? expected) =>
        Assert.Equal(expected, McpEndpoint.ClientTitle(new Implementation { Name = name, Title = title, Version = "1" }));

    [Fact]
    public async Task A_pipe_left_by_a_crashed_editor_is_taken_over()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows removes a pipe with its process.");
        var server = new McpPipeServer(new FakeEditor(), Connection.NewPipeName());
        // On Unix a pipe is a socket file, which stays behind when its editor is killed.
        string socket = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + server.PipeName);
        await File.WriteAllTextAsync(socket, "", TestContext.Current.CancellationToken);
        await using (server)
        {
            await server.Start().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await using var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(30_000, TestContext.Current.CancellationToken);
            await WaitUntil(() => server.Sessions == 1);
        }
    }

    /// <summary>Polls for up to 15 s, which a busy CI runner running the other test assemblies alongside may need.</summary>
    internal static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }
}

/// <summary>Silences and scene changes: finding them, and cutting pauses out.</summary>
public class McpAnalysisTests
{
    private static readonly int[] SecondTrack = [2], ThirdTrack = [3];
    [Fact]
    public async Task Find_silences_lists_pauses_of_at_least_the_minimum_length()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);

        var found = (await c.Client.Call("find_silences")).Json();
        Assert.Equal(3, found.GetProperty("count").GetInt32());
        Assert.Equal(16.5, found.GetProperty("total").GetDouble());
        Assert.Equal(-48, found.GetProperty("thresholdDb").GetDouble());
        Assert.Equal(-60, found.GetProperty("noiseFloorDb").GetDouble());
        Assert.True(found.GetProperty("complete").GetBoolean());
        var first = found.GetProperty("silences")[0];
        Assert.Equal((50, 52.5, 2.5, "00:50.000–00:52.500"),
            (first.GetProperty("start").GetDouble(), first.GetProperty("end").GetDouble(), first.GetProperty("duration").GetDouble(),
                first.GetProperty("range").GetString()));

        var shorter = (await c.Client.Call("find_silences", new { minDuration = 0.5, thresholdDb = -35, tracks = SecondTrack, start = 100, end = 200 })).Json();
        Assert.Equal([120.0, 150.0], shorter.GetProperty("silences").EnumerateArray().Select(x => x.GetProperty("start").GetDouble()));
        Assert.Equal((0.5, (double?)-35), (editor.SilenceQueries[^1].MinDuration, editor.SilenceQueries[^1].ThresholdDb));
        Assert.Equal([1], editor.SilenceQueries[^1].Streams!);

        Assert.Contains("no audio track 3", (await c.Client.Call("find_silences", new { tracks = ThirdTrack })).Text(), StringComparison.Ordinal);
        editor.Silences = null;
        Assert.Contains("no audio", (await c.Client.Call("find_silences")).Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Find_scene_changes_reports_progress_while_detection_runs()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);

        var all = (await c.Client.Call("find_scene_changes")).Json();
        Assert.Equal([20, 105.5, 180, 420], all.GetProperty("changes").EnumerateArray().Select(x => x.GetDouble()));
        Assert.Equal(10, all.GetProperty("threshold").GetDouble());
        var some = (await c.Client.Call("find_scene_changes", new { threshold = 30, start = 0, end = 100 })).Json();
        Assert.Equal([20.0], some.GetProperty("changes").EnumerateArray().Select(x => x.GetDouble()));

        editor.Scenes = new SceneReport([20], false, 0.25);
        var partial = (await c.Client.Call("find_scene_changes")).Json();
        Assert.False(partial.GetProperty("complete").GetBoolean());
        Assert.Contains("25% done", partial.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.True((await c.Client.Call("find_scene_changes", new { threshold = 0 })).IsError);
    }

    [Fact]
    public async Task Cut_silences_cuts_pauses_out_of_included_clips_as_one_edit()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);

        // 120–124 is inside Demo (100–200); 330–340 is inside the excluded Q&A; 50–52.5 is in no clip.
        var cut = (await c.Client.Call("cut_silences")).Json();

        Assert.Equal("Removed 1 silence: 3.7 s shorter.", cut.GetProperty("result").GetString());
        var clips = cut.GetProperty("clips").EnumerateArray()
            .Select(x => (x.GetProperty("label").GetString(), x.GetProperty("start").GetDouble(), x.GetProperty("end").GetDouble())).ToList();
        Assert.Equal([("Intro", 10, 40), ("Demo", 100, 120.15), ("Demo (2)", 123.85, 200), ("Q&A", 300, 360)], clips);
        var entry = Assert.Single(editor.Session.History.Entries);
        Assert.Equal((EditOrigin.Assistant, "Removed 1 silence", "cut_silences"), (entry.Origin, entry.Description, entry.Command.Name));

        var again = (await c.Client.Call("cut_silences", new { minDuration = 5 })).Json();
        Assert.Contains("nothing changed", again.GetProperty("result").GetString(), StringComparison.Ordinal);
        Assert.False(again.TryGetProperty("action", out _));
    }

    [Fact]
    public async Task Cut_silences_without_clips_keeps_the_whole_video_first()
    {
        var editor = new FakeEditor();
        editor.Open(FakeEditor.Sample with { Clips = [] });
        await using var c = await Connection.OpenAsync(editor);

        var cut = (await c.Client.Call("cut_silences", new { padding = 0 })).Json();

        Assert.Equal([(0.0, 50.0), (52.5, 120.0), (124.0, 330.0), (340.0, 600.0)], cut.GetProperty("clips").EnumerateArray()
            .Select(x => (x.GetProperty("start").GetDouble(), x.GetProperty("end").GetDouble())));
        Assert.Equal("keynote (4)", cut.GetProperty("clips")[3].GetProperty("label").GetString());
        // Measured against the whole video, which was kept first.
        Assert.Equal("Removed 3 silences: 16.5 s shorter.", cut.GetProperty("result").GetString());
        Assert.Single(editor.Session.History.Entries);
    }

    [Fact]
    public async Task Cut_silences_waits_for_the_audio_analysis()
    {
        var editor = new FakeEditor { AnalysisStatus = "analysing 40%" };
        editor.Silences = editor.Silences! with { IsComplete = false };
        await using var c = await Connection.OpenAsync(editor);

        var result = await c.Client.Call("cut_silences");
        Assert.True(result.IsError);
        Assert.Contains("analysing 40%", result.Text(), StringComparison.Ordinal);
        Assert.Empty(editor.Session.History.Entries);
    }
}

/// <summary>Reading, searching and cutting by the transcript.</summary>
public class McpTranscriptTests
{
    private static readonly string[] So = ["so"];

    [Fact]
    public async Task Get_transcript_gives_timed_sentences_and_words()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);

        var all = (await c.Client.Call("get_transcript")).Json();
        Assert.Equal("done", all.GetProperty("status").GetString());
        Assert.Equal("parakeet-tdt-0.6b-v3", all.GetProperty("model").GetString());
        Assert.Equal(
        [
            "00:10.000–00:11.400 Welcome to the keynote.",
            "00:12.000–00:14.400 Um, so today we export videos.",
            "01:45.000–01:46.700 Uh the export dialog.",
        ], all.GetProperty("lines").EnumerateArray().Select(l => l.GetString()));
        Assert.False(all.TryGetProperty("words", out _));

        var part = (await c.Client.Call("get_transcript", new { start = 100, words = true })).Json();
        Assert.Single(part.GetProperty("lines").EnumerateArray());
        Assert.Equal(["Uh", "the", "export", "dialog."], part.GetProperty("words").EnumerateArray().Select(w => w.GetProperty("text").GetString()));
        Assert.Equal("done", (await c.Client.Call("get_project")).Json().GetProperty("transcript").GetString());
    }

    [Fact]
    public async Task Get_transcript_starts_transcription_and_reports_progress()
    {
        var editor = new FakeEditor();
        editor.TranscriptStatus = new TranscriptStatus("none", 0, null, null);
        await using var c = await Connection.OpenAsync(editor);

        var waiting = (await c.Client.Call("get_transcript")).Json();
        Assert.Equal(1, editor.TranscriptionStarts);
        Assert.Equal("waiting for the rest of the analysis", waiting.GetProperty("status").GetString());
        Assert.Empty(waiting.GetProperty("lines").EnumerateArray());

        editor.TranscriptStatus = new TranscriptStatus("running", 0.4, new Transcript("m", "auto", [new("Hi.", 1, 1.3)]), null);
        var running = (await c.Client.Call("get_transcript")).Json();
        Assert.Equal("transcribing 40%", running.GetProperty("status").GetString());
        Assert.Contains("still running", running.GetProperty("note").GetString(), StringComparison.Ordinal);

        editor.TranscriptStatus = new TranscriptStatus("none", 0, null, null);
        editor.TranscriptionRefusal = "No transcription model is installed.";
        Assert.Contains("No transcription model", (await c.Client.Call("get_transcript")).Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_finds_words_and_phrases_ignoring_case_and_punctuation()
    {
        await using var c = await Connection.OpenAsync(new FakeEditor());

        var export = (await c.Client.Call("search_transcript", new { text = "EXPORT" })).Json();
        Assert.Equal(2, export.GetProperty("count").GetInt32());
        var first = export.GetProperty("matches")[0];
        Assert.Equal((13.4, "export", "Um, so today we export videos."),
            (first.GetProperty("start").GetDouble(), first.GetProperty("text").GetString(), first.GetProperty("context").GetString()));

        var phrase = (await c.Client.Call("search_transcript", new { text = "the export dialog" })).Json();
        Assert.Equal("01:45.500–01:46.700", Assert.Single(phrase.GetProperty("matches").EnumerateArray()).GetProperty("range").GetString());

        var none = (await c.Client.Call("search_transcript", new { text = "smart cut" })).Json();
        Assert.Equal(0, none.GetProperty("count").GetInt32());
        Assert.Contains("not in the transcript", none.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filler_words_are_found_and_cut_as_one_edit()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);

        var fillers = (await c.Client.Call("find_filler_words")).Json();
        Assert.Equal(["Um,", "Uh"], fillers.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("text").GetString()));
        Assert.Equal(0.6, fillers.GetProperty("total").GetDouble(), 6);
        Assert.Equal(1, (await c.Client.Call("find_filler_words", new { words = So })).Json().GetProperty("count").GetInt32());

        var cut = (await c.Client.Call("cut_filler_words", new { padding = 0 })).Json();

        Assert.StartsWith("Removed 2 filler words", cut.GetProperty("result").GetString(), StringComparison.Ordinal);
        Assert.Equal([(10.0, 12.0), (12.3, 40.0), (100.0, 105.0), (105.3, 200.0), (300.0, 360.0)], cut.GetProperty("clips").EnumerateArray()
            .Select(x => (x.GetProperty("start").GetDouble(), x.GetProperty("end").GetDouble())));
        Assert.Equal("cut_filler_words", Assert.Single(editor.Session.History.Entries).Command.Name);
    }

    [Fact]
    public async Task Filler_words_are_the_users_own()
    {
        var editor = new FakeEditor { FillerWords = ["so", "the"] };
        await using var c = await Connection.OpenAsync(editor);

        var fillers = (await c.Client.Call("find_filler_words")).Json();

        Assert.Equal(["the", "so", "the"], fillers.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task Cut_ranges_cuts_what_claude_picked_and_checks_the_ranges()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);

        var cut = (await c.Client.Call("cut_ranges", new { ranges = new[] { new { start = 12.0, end = 14.4 } }, description = "Removed the aside" })).Json();

        Assert.StartsWith("Removed the aside", cut.GetProperty("result").GetString(), StringComparison.Ordinal);
        Assert.Equal("Removed the aside", Assert.Single(editor.Session.History.Entries).Description);
        Assert.True((await c.Client.Call("cut_ranges", new { ranges = new[] { new { start = 5.0, end = 4.0 } }, description = "x" })).IsError);
        var outside = (await c.Client.Call("cut_ranges", new { ranges = new[] { new { start = 60.0, end = 70.0 } }, description = "x" })).Json();
        Assert.Contains("nothing changed", outside.GetProperty("result").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cutting_filler_words_waits_for_the_whole_transcript()
    {
        var editor = new FakeEditor();
        editor.TranscriptStatus = editor.TranscriptStatus with { State = "running", Progress = 0.5 };
        await using var c = await Connection.OpenAsync(editor);

        var result = await c.Client.Call("cut_filler_words");
        Assert.True(result.IsError);
        Assert.Contains("transcribing 50%", result.Text(), StringComparison.Ordinal);
    }
}

/// <summary>Claude exporting: the choices it passes, waiting, progress and cancelling.</summary>
public class McpExportTests
{
    static McpExportTests() => EditorTools.ExportWait = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Export_passes_the_choices_and_waits_for_the_result()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);
        string folder = Path.GetTempPath();

        var done = (await c.Client.Call("export", new { mode = "Lossless", container = "MKV", merge = false, folder, allTracks = false })).Json();

        Assert.Equal("done", done.GetProperty("status").GetString());
        Assert.Equal(1, done.GetProperty("progress").GetDouble());
        Assert.Equal(["/videos/keynote-cut.mp4"], done.GetProperty("files").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(new ExportRequest("lossless", "mkv", false, folder, null, false, null, null), editor.LastExport);
        Assert.Equal("done", (await c.Client.Call("get_export_status")).Json().GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_long_export_reports_progress_and_can_be_cancelled()
    {
        var editor = new FakeEditor { ExportReads = int.MaxValue };
        await using var c = await Connection.OpenAsync(editor);

        var running = (await c.Client.Call("export")).Json();
        Assert.Equal("running", running.GetProperty("status").GetString());
        Assert.Contains("get_export_status", running.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Equal(new ExportRequest(), editor.LastExport);

        Assert.Equal("cancelled", (await c.Client.Call("cancel_export")).Json().GetProperty("status").GetString());
        Assert.True((await c.Client.Call("cancel_export")).IsError);
    }

    [Fact]
    public async Task Bad_choices_and_refusals_come_back_as_errors()
    {
        var editor = new FakeEditor();
        await using var c = await Connection.OpenAsync(editor);

        Assert.Contains("lossless, reencode", (await c.Client.Call("export", new { mode = "fast" })).Text(), StringComparison.Ordinal);
        Assert.Contains("full path", (await c.Client.Call("export", new { folder = "out" })).Text(), StringComparison.Ordinal);
        Assert.True((await c.Client.Call("get_export_status")).IsError);
        editor.ExportRefusal = "There are no included clips to export.";
        Assert.Contains("no included clips", (await c.Client.Call("export")).Text(), StringComparison.Ordinal);
        Assert.Null(editor.LastExport);
    }
}

/// <summary>The stdio bridge Claude starts: lists tools without the editor, starts it on the first call.</summary>
public class McpBridgeTests
{
    private sealed class BridgeRun : IAsyncDisposable
    {
        private readonly System.IO.Pipelines.Pipe _toBridge = new(), _fromBridge = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _running;

        public BridgeRun(McpBridge bridge)
        {
            Bridge = bridge;
            _running = bridge.RunAsync(_toBridge.Reader.AsStream(), _fromBridge.Writer.AsStream(), _stop.Token);
        }

        public McpBridge Bridge { get; }
        public McpClient? Client { get; private set; }

        public async Task<McpClient> ConnectAsync(Implementation? clientInfo = null) => Client = await McpClient.CreateAsync(
            new StreamClientTransport(_toBridge.Writer.AsStream(), _fromBridge.Reader.AsStream()),
            clientInfo is null ? null : new McpClientOptions { ClientInfo = clientInfo },
            cancellationToken: TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Client is not null)
                await Client.DisposeAsync();
            await _stop.CancelAsync();
            try
            {
                await _running;
            }
            catch (OperationCanceledException)
            {
            }
            await Bridge.DisposeAsync();
        }
    }

    [Fact]
    public async Task Tools_are_listed_without_starting_the_editor_and_calls_start_it()
    {
        string pipeName = Connection.NewPipeName();
        var editor = new FakeEditor();
        McpPipeServer? server = null;
        int launches = 0;
        await using var run = new BridgeRun(new McpBridge(() =>
        {
            launches++;
            server = new McpPipeServer(editor, pipeName);
            server.Start();
            return true;
        }, pipeName, TimeSpan.FromSeconds(30)));
        var client = await run.ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, t => t.Name == "edit_timeline");
        Assert.Equal(0, launches);

        var result = (await client.Call("set_label", new { clip = 1, label = "Hello" })).Json();
        Assert.Equal(1, launches);
        Assert.Equal("Hello", editor.Session.Project.Get(1).Label);
        Assert.Equal("Hello", result.GetProperty("clips")[0].GetProperty("label").GetString());

        // The editor is closed and opened again: the next call reconnects (starting it once more).
        await server!.DisposeAsync();
        var second = (await client.Call("get_project")).Json();
        Assert.Equal("keynote", second.GetProperty("name").GetString());
        Assert.Equal(2, launches);
        await server.DisposeAsync();
    }

    [Fact]
    public async Task A_running_editor_is_used_without_launching()
    {
        string pipeName = Connection.NewPipeName();
        await using var server = new McpPipeServer(new FakeEditor(), pipeName);
        // Listening before the bridge looks: it only tries briefly before it would launch an editor.
        await server.Start().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await using var run = new BridgeRun(new McpBridge(() => throw new InvalidOperationException("should not launch"), pipeName));
        var client = await run.ConnectAsync();

        Assert.Equal("keynote", (await client.Call("get_project")).Json().GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_editor_sees_who_is_connected_through_the_bridge()
    {
        string pipeName = Connection.NewPipeName();
        await using var server = new McpPipeServer(new FakeEditor(), pipeName);
        await server.Start().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await using var run = new BridgeRun(new McpBridge(() => false, pipeName));
        var client = await run.ConnectAsync(new Implementation { Name = "claude-ai", Version = "0.14.0" });

        (await client.Call("get_project")).Json();

        await McpEditingTests.WaitUntil(() => server.ClientName == "Claude Desktop");
    }

    [Fact]
    public async Task An_editor_that_does_not_start_is_reported()
    {
        await using var run = new BridgeRun(new McpBridge(() => false, Connection.NewPipeName()));
        var client = await run.ConnectAsync();

        var result = await client.Call("get_project");
        Assert.True(result.IsError);
        Assert.Contains("could not be started", result.Text(), StringComparison.Ordinal);
    }
}
