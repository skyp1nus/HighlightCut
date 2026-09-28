using System.ComponentModel;
using Avalonia.Threading;
using OurCut.App.ViewModels;
using OurCut.Mcp;

namespace OurCut.App.Services;

/// <summary>
/// Runs the editor's MCP server while "Let Claude connect" is on (<see cref="ClaudePanelViewModel.IsServerOn"/>), and
/// shows its state in the title bar, the Claude panel and Settings → MCP server.
/// </summary>
public sealed class EditorMcpServer : IAsyncDisposable
{
    private readonly EditorViewModel _editor;
    private readonly EditorMcpHost _host;
    private readonly string? _pipeName;
    private McpPipeServer? _server;
    private Task _switching = Task.CompletedTask;
    private bool _started;
    private bool _disposed;

    public EditorMcpServer(EditorViewModel editor, string? pipeName = null)
    {
        _editor = editor;
        _host = new EditorMcpHost(editor);
        _pipeName = pipeName;
        PipeName = pipeName ?? McpEndpoint.PipeName;
        editor.Claude.PropertyChanged += OnClaudeChanged;
        editor.PropertyChanged += OnEditorChanged;
    }

    public string PipeName { get; }

    public void Start()
    {
        _started = true;
        if (_editor.Claude.IsServerOn)
            Listen();
        Refresh();
    }

    private void Listen()
    {
        if (_server is not null)
            return;
        var server = new McpPipeServer(_host, _pipeName) { OwnerLabel = OwnerLabel };
        server.StateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _server = server;
        _ = server.Start();
    }

    private void OnClaudeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_started && e.PropertyName == nameof(ClaudePanelViewModel.IsServerOn))
            _switching = SwitchAsync(_switching);
    }

    /// <summary>What another OurCut window says about this one while it has the server: the project file, or the video.</summary>
    private string? OwnerLabel => !_editor.HasFile ? null
        : _editor.ProjectPath is { } project ? Path.GetFileName(project)
        : _editor.MediaFileName;

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_server is { } server && e.PropertyName is nameof(EditorViewModel.Media) or nameof(EditorViewModel.ProjectPath)
                or nameof(EditorViewModel.MediaFileName))
            server.OwnerLabel = OwnerLabel;
    }

    // One switch at a time: a quick off and on waits until the old server has let go of the pipe.
    private async Task SwitchAsync(Task previous)
    {
        await previous.ConfigureAwait(true);
        if (_disposed)
            return;
        if (_editor.Claude.IsServerOn)
        {
            Listen();
        }
        else if (_server is { } server)
        {
            _server = null;
            Refresh();
            await server.DisposeAsync().ConfigureAwait(true);
        }
        Refresh();
    }

    private void Refresh()
    {
        var claude = _editor.Claude;
        var server = _server;
        bool connected = server is { Sessions: > 0 };
        claude.IsListening = server?.IsListening ?? false;
        claude.IsServedElsewhere = server?.IsInUseElsewhere ?? false;
        claude.OtherWindowProject = claude.IsServedElsewhere ? server?.OtherOwnerLabel : null;
        if (connected != claude.IsConnected)
            claude.ConnectedSince = connected ? DateTimeOffset.Now : null;
        claude.IsConnected = connected;
        claude.ClientName = connected ? server?.ClientName : null;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _editor.Claude.PropertyChanged -= OnClaudeChanged;
        _editor.PropertyChanged -= OnEditorChanged;
        if (_server is { } server)
            await server.DisposeAsync().ConfigureAwait(false);
    }
}
