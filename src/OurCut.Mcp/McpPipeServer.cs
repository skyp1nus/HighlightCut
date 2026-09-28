using System.IO.Pipes;
using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OurCut.Mcp;

/// <summary>Where the editor listens for MCP connections: a named pipe only the current user can open.</summary>
public static class McpEndpoint
{
    public static string PipeName { get; } =
        "ourcut-mcp-" + new string([.. Environment.UserName.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')]);

    public static string Version { get; } =
        typeof(McpEndpoint).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static Implementation ServerInfo => new() { Name = EditorTools.ServerName, Title = "OurCut", Version = Version };

    /// <summary>How the editor names a connected client ("Claude Desktop"); null for the bridge or no name.</summary>
    public static string? ClientTitle(Implementation? client) => client switch
    {
        { Title: { Length: > 0 } title } => title,
        { Name: "claude-ai" } => "Claude Desktop",
        { Name: "claude-code" } => "Claude Code",
        { Name: McpBridge.BridgeName } => null,
        { Name: { Length: > 0 } name } => name,
        _ => null,
    };
}

/// <summary>
/// The editor's MCP server. Every connection on the pipe (normally one: the <c>OurCut.exe mcp</c> bridge
/// that Claude starts) gets its own MCP session with the editor tools. Only one editor serves a pipe name,
/// the one holding its lock file: a second OurCut window waits until the first one closes.
/// </summary>
public sealed class McpPipeServer(IEditorHost host, string? pipeName = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _listeningOnce = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<McpServerTool> _tools = EditorTools.Create(host);
    private Task? _listening;
    private int _sessions;
    private int _disposed;
    private volatile bool _isListening;
    private volatile bool _inUse;
    private FileStream? _lock;
    private readonly List<string> _clients = [];
    private readonly Lock _ownerGate = new();
    private string? _ownerLabel;
    private volatile string? _otherOwner;

    public string PipeName { get; } = pipeName ?? McpEndpoint.PipeName;

    /// <summary>
    /// Held open (exclusively) by the editor that serves <see cref="PipeName"/>; the system lets go of it
    /// when that editor exits, even if it crashes. On Unix the pipe is a socket file that a crashed editor
    /// leaves behind, so the pipe itself cannot tell whether its owner is still running.
    /// </summary>
    public string LockPath => Path.Combine(Path.GetTempPath(), PipeName + ".lock");

    /// <summary>Written by the editor that serves the pipe with <see cref="OwnerLabel"/>, for the windows that wait.</summary>
    public string OwnerPath => Path.Combine(Path.GetTempPath(), PipeName + ".owner");

    /// <summary>What a waiting OurCut window shows about this one while it serves the pipe: the open project.</summary>
    public string? OwnerLabel
    {
        get
        {
            lock (_ownerGate)
                return _ownerLabel;
        }
        set
        {
            lock (_ownerGate)
            {
                _ownerLabel = value;
                if (_lock is not null)
                    WriteOwner();
            }
        }
    }

    /// <summary>The <see cref="OwnerLabel"/> of the editor that serves the pipe, while <see cref="IsInUseElsewhere"/>.</summary>
    public string? OtherOwnerLabel => _otherOwner;

    /// <summary>The client of the newest open session ("Claude Desktop"), once it has said who it is.</summary>
    public string? ClientName
    {
        get
        {
            lock (_clients)
                return _clients.Count > 0 ? _clients[^1] : null;
        }
    }

    /// <summary>How often a second editor checks whether the first one has closed.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Open MCP sessions.</summary>
    public int Sessions => Volatile.Read(ref _sessions);

    /// <summary>Claude can connect.</summary>
    public bool IsListening => _isListening;

    /// <summary>Another editor (another OurCut window) serves the pipe; this one waits for it to close.</summary>
    public bool IsInUseElsewhere => _inUse;

    /// <summary>Raised on a background thread when a session starts or ends, or listening starts or stops.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Starts listening in the background. The task completes once Claude can connect (while another editor serves the
    /// pipe, when that one closes), and is cancelled if the server stops first.
    /// </summary>
    public Task Start()
    {
        _listening ??= Task.Run(() => ListenAsync(_stop.Token));
        return _listeningOnce.Task;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!TryClaimName() || TryCreatePipe(first: !_isListening) is not { } pipe)
                {
                    // Another editor has the pipe (or it could not be created): try again in a while.
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                    continue;
                }
                SetState(listening: true, inUse: false);
                _listeningOnce.TrySetResult();
                try
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                _ = Task.Run(() => ServeAsync(pipe, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetState(listening: false, inUse: false);
            _listeningOnce.TrySetCanceled(CancellationToken.None);
            lock (_ownerGate)
            {
                if (_lock is not null)
                {
                    // Before letting go: the next editor writes its own.
                    TryDelete(OwnerPath);
                    _lock.Dispose();
                    _lock = null;
                }
            }
        }
    }

    /// <summary>Takes the lock file, unless another editor has it.</summary>
    private bool TryClaimName()
    {
        if (_lock is not null)
            return true;
        try
        {
            lock (_ownerGate)
            {
                _lock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                _otherOwner = null;
                WriteOwner();
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            string? owner = ReadOwner();
            bool changed = owner != _otherOwner;
            _otherOwner = owner;
            SetState(listening: false, inUse: true);
            if (changed)
                StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
    }

    /// <summary>Under <see cref="_ownerGate"/>, while holding the lock.</summary>
    private void WriteOwner()
    {
        try
        {
            File.WriteAllText(OwnerPath, _ownerLabel ?? "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Waiting windows then say "another OurCut window".
        }
    }

    private string? ReadOwner()
    {
        try
        {
            return File.ReadAllText(OwnerPath).Trim() is { Length: > 0 } label ? label : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Being written (Windows), or never written: what was read last stands.
            return _otherOwner;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Later instances wait for the next connection while earlier ones serve. On Windows the first one also
    /// claims the name (<see cref="PipeOptions.FirstPipeInstance"/>) against other programs; on Unix that
    /// option fails on a socket file left by a crashed editor, and the lock file already decides.
    /// </summary>
    private NamedPipeServerStream? TryCreatePipe(bool first)
    {
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
            | (first && OperatingSystem.IsWindows() ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        try
        {
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetState(listening: false, inUse: first);
            return null;
        }
    }

    private void SetState(bool listening, bool inUse)
    {
        if (_isListening == listening && _inUse == inUse)
            return;
        _isListening = listening;
        _inUse = inUse;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        Interlocked.Increment(ref _sessions);
        StateChanged?.Invoke(this, EventArgs.Empty);
        string? client = null;
        try
        {
            var options = new McpServerOptions
            {
                ServerInfo = McpEndpoint.ServerInfo,
                ServerInstructions = EditorTools.Instructions,
                ToolCollection = [.. _tools],
                Filters = new McpServerFilters
                {
                    Message = new McpMessageFilters
                    {
                        // After the handshake the client has said who it is.
                        IncomingFilters =
                        [
                            next => async (context, cancellationToken) =>
                            {
                                await next(context, cancellationToken).ConfigureAwait(false);
                                if (client is null && McpEndpoint.ClientTitle(context.Server.ClientInfo) is { } name)
                                {
                                    client = name;
                                    lock (_clients)
                                        _clients.Add(name);
                                    StateChanged?.Invoke(this, EventArgs.Empty);
                                }
                            },
                        ],
                    },
                },
            };
            await using var server = McpServer.Create(new StreamServerTransport(pipe, pipe, EditorTools.ServerName), options);
            await server.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The bridge went away or the editor is closing.
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            if (client is not null)
            {
                lock (_clients)
                    _clients.Remove(client);
            }
            Interlocked.Decrement(ref _sessions);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_listening is { } listening)
            await listening.ConfigureAwait(false);
        _stop.Dispose();
    }
}
