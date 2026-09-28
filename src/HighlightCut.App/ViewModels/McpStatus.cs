namespace HighlightCut.App.ViewModels;

/// <summary>The MCP server as the title-bar badge and Settings → MCP server show it.</summary>
public enum McpStatus
{
    /// <summary>Not running: turned off, or not started (demo runs have no server).</summary>
    Off,

    /// <summary>Listening; Claude has not connected.</summary>
    Waiting,

    /// <summary>Claude is connected.</summary>
    Connected,

    /// <summary>Claude is connected and editing right now.</summary>
    Editing,

    /// <summary>Another HighlightCut window has the server.</summary>
    OtherWindow,
}
