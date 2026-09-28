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
    internal async ValueTask<CallToolResult> CallAsync(CallToolRequestParams request, Implementation? caller, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            var client = await ConnectAsync(caller, cancellationToken).ConfigureAwait(false);
            try
            {
                return await client.CallToolAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (attempt == 0 && e is not OperationCanceledException and not McpException)
            {
                // The editor was closed since the last call: connect again (starting it if needed).
                await DisconnectAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<McpClient> ConnectAsync(Implementation? caller, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { } connected)
                return connected;
            var pipe = await TryConnectAsync(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
            if (pipe is null)
            {
                if (!_launchEditor())
                    throw new McpException("HighlightCut could not be started.");
                pipe = await TryConnectAsync(_startTimeout, cancellationToken).ConfigureAwait(false)
                    ?? throw new McpException("HighlightCut did not start in time. Open it and try again.");
            }
            _pipe = pipe;
            // The editor is told who is really connected, not the bridge.
            var info = caller is null
                ? new Implementation { Name = BridgeName, Version = McpEndpoint.Version }
                : new Implementation { Name = caller.Name, Title = caller.Title, Version = caller.Version };
            _client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), new McpClientOptions { ClientInfo = info },
                cancellationToken: cancellationToken).ConfigureAwait(false);
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

    private async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is not null)
                await _client.DisposeAsync().ConfigureAwait(false);
            if (_pipe is not null)
                await _pipe.DisposeAsync().ConfigureAwait(false);
            _client = null;
            _pipe = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

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
