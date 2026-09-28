using CommunityToolkit.Mvvm.ComponentModel;

namespace HighlightCut.App.ViewModels;

/// <summary>The MCP connection as one state, for the title bar and Settings → MCP server.</summary>
public sealed partial class ClaudePanelViewModel
{
    /// <summary>"Let Claude connect" (Settings → MCP server): the editor's MCP server should run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    public partial bool IsServerOn { get; set; } = true;

    /// <summary>When the current connection started; null while there is none.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? ConnectedSince { get; set; }

    /// <summary>The connected MCP client ("Claude Desktop"), as its clientInfo names it; null reads as "Claude".</summary>
    [ObservableProperty]
    public partial string? ClientName { get; set; }

    /// <summary>The project open in the HighlightCut window that has the server (from its owner file next to the pipe lock).</summary>
    [ObservableProperty]
    public partial string? OtherWindowProject { get; set; }

    /// <summary>
    /// Follows <see cref="IsServerOn"/>, <see cref="IsConnected"/>, <see cref="IsWorking"/>, <see cref="IsListening"/> and
    /// <see cref="IsServedElsewhere"/>.
    /// </summary>
    public McpStatus Status => !IsServerOn ? McpStatus.Off
        : IsConnected ? (IsWorking ? McpStatus.Editing : McpStatus.Connected)
        : IsListening ? McpStatus.Waiting
        : IsServedElsewhere ? McpStatus.OtherWindow
        : McpStatus.Off;
}
