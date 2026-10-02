using System.IO.Pipes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HighlightCut.Mcp;

/// <summary>
/// What <c>HighlightCut.exe mcp</c> runs: the MCP server Claude starts over stdio. It lists the editor's tools
/// itself and forwards each call to the running editor over the pipe, starting HighlightCut only when a tool
/// is first used (Claude Desktop starts its MCP servers with itself, and HighlightCut should not pop up then).
/// </summary>
public sealed class McpBridge : IAsyncDisposable
{
    /// <summary>The bridge's own client name, used when Claude did not say who it is.</summary>
    internal const string BridgeName = "highlightcut-bridge";

    private readonly Func<bool> _launchEditor;
    private readonly string _pipeName;
    private readonly TimeSpan _startTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private McpClient? _client;

    /// <param name="launchEditor">Starts HighlightCut; returns false if it could not.</param>
    public McpBridge(Func<bool> launchEditor, string? pipeName = null, TimeSpan? startTimeout = null)
    {
        _launchEditor = launchEditor;
        _pipeName = pipeName ?? McpEndpoint.PipeName;
        _startTimeout = startTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Serves MCP on stdio until Claude closes it.</summary>
    public static async Task<int> RunStdioAsync(Func<bool> launchEditor, CancellationToken cancellationToken = default)
    {
        await using var bridge = new McpBridge(launchEditor);
        await bridge.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        // The definitions come from the same tools the editor serves, so the two never drift apart.
        var options = new McpServerOptions
        {
            ServerInfo = McpEndpoint.ServerInfo,
            ServerInstructions = EditorTools.Instructions,
            ToolCollection = [.. EditorTools.Create(NoEditor.Instance).Select(t => new ForwardedTool(t.ProtocolTool, this))],
        };
        await using var server = McpServer.Create(new StreamServerTransport(input, output, EditorTools.ServerName), options);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <param name="caller">Who called the tool (Claude Desktop, Claude Code): the editor shows it as connected.</param>
    /// <remarks>
    /// While the editor closes or starts, a connection can drop at any point until it answers: on the last call's
    /// connection, on a pipe that is going away, or during the handshake. Those are tried again until the start
    /// timeout; an error from the editor itself (an error result or <see cref="McpProtocolException"/>) is Claude's.
    /// </remarks>
    internal async ValueTask<CallToolResult> CallAsync(CallToolRequestParams request, Implementation? caller, CancellationToken cancellationToken)
    {
        var call = new CallState(DateTime.UtcNow + _startTimeout);
        while (true)
        {
            McpClient? client = null;
            try
            {
                client = await ConnectAsync(caller, call, cancellationToken).ConfigureAwait(false);
                return await client.CallToolAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (IsConnectionFailure(e) && !cancellationToken.IsCancellationRequested)
            {
                if (client is not null)
                    await DisconnectAsync(client).ConfigureAwait(false);
                if (DateTime.UtcNow + RetryDelay >= call.Deadline)
                    throw new EditorUnreachableException("Lost the connection to HighlightCut. Check that it’s open and try again.", e);
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>How long a call waits before it connects again after the connection dropped.</summary>
    internal static TimeSpan RetryDelay { get; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The connection broke: not the editor's answer, not a cancellation and not the bridge giving up.</summary>
    private static bool IsConnectionFailure(Exception e) => e switch
    {
        EditorUnreachableException or McpProtocolException or OperationCanceledException => false,
        // ClientTransportClosedException is an IOException; an McpException is also thrown when the transport closes.
        IOException or McpException or ObjectDisposedException => true,
        _ => false,
    };

    /// <summary>Returns the open connection or opens one, starting the editor at most once per call and only when it is not running.</summary>
    private async Task<McpClient> ConnectAsync(Implementation? caller, CallState call, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { } connected)
            {
                if (!connected.Completion.IsCompleted)
                    return connected;
                // The editor closed the connection since the last call.
                await DropAsync().ConfigureAwait(false);
            }
            var pipe = await TryConnectAsync(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
            if (pipe is null)
            {
                // A running editor (one that holds the pipe's lock) is closing or still starting: wait for it, never start another.
                bool start = !call.Launched && !McpPipeServer.IsServed(_pipeName);
                if (start)
                {
                    call.Launched = true;
                    if (!_launchEditor())
                        throw new EditorUnreachableException("HighlightCut could not be started.");
                }
                pipe = await TryConnectAsync(call.Deadline - DateTime.UtcNow, cancellationToken).ConfigureAwait(false)
                    ?? throw new EditorUnreachableException(call.Launched
                        ? "HighlightCut did not start in time. Open it and try again."
                        : "HighlightCut is open but didn’t answer in time. Try again in a moment.");
            }
            // The editor is told who is really connected, not the bridge.
            var info = caller is null
                ? new Implementation { Name = BridgeName, Version = McpEndpoint.Version }
                : new Implementation { Name = caller.Name, Title = caller.Title, Version = caller.Version };
            try
            {
                _client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), new McpClientOptions { ClientInfo = info },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            _pipe = pipe;
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NamedPipeClientStream?> TryConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                var left = deadline - DateTime.UtcNow;
                await pipe.ConnectAsync((int)Math.Clamp(left.TotalMilliseconds, 50, 1000), cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch (Exception e) when (e is TimeoutException or IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (DateTime.UtcNow >= deadline)
                    return null;
                // On Unix a missing socket fails at once; wait a little before the next try.
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Closes the connection if it is still <paramref name="failed"/>: another call may have opened a new one since.</summary>
    private async Task DisconnectAsync(McpClient? failed = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (failed is null || _client == failed)
                await DropAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Under <see cref="_gate"/>.</summary>
    private async Task DropAsync()
    {
        var client = _client;
        var pipe = _pipe;
        _client = null;
        _pipe = null;
        try
        {
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Closing a connection the editor already dropped.
        }
        if (pipe is not null)
            await pipe.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>One tool call's connection attempts: until when, and whether it started the editor.</summary>
    private sealed class CallState(DateTime deadline)
    {
        public DateTime Deadline { get; } = deadline;
        public bool Launched { get; set; }
    }

    /// <summary>The bridge gives up on the editor; Claude is shown the message.</summary>
    private sealed class EditorUnreachableException(string message, Exception? inner = null) : McpException(message, inner);

    /// <summary>A tool whose definition is the editor's and whose calls go to the editor.</summary>
    private sealed class ForwardedTool(Tool tool, McpBridge bridge) : McpServerTool
    {
        public override Tool ProtocolTool => tool;
        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default) =>
            bridge.CallAsync(request.Params ?? new CallToolRequestParams { Name = tool.Name }, request.Server.ClientInfo, cancellationToken);
    }

    /// <summary>Only used to build the tool definitions; never called.</summary>
    private sealed class NoEditor : IEditorHost
    {
        public static NoEditor Instance { get; } = new();

        public Task<T> RunAsync<T>(Func<IEditorContext, Task<T>> action) =>
            throw new InvalidOperationException("The bridge forwards tool calls to the editor.");
    }
}
