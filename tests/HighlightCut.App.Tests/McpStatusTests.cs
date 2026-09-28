using Avalonia.Headless.XUnit;
using HighlightCut.App.Demo;
using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Tests;

/// <summary>The MCP connection as one state (title-bar badge, Settings → MCP server).</summary>
public class McpStatusTests
{
    [AvaloniaFact]
    public void The_status_follows_the_connection_flags()
    {
        var claude = App.CreateEditor(null).Claude;
        Assert.Equal(McpStatus.Off, claude.Status);

        claude.IsServedElsewhere = true;
        Assert.Equal(McpStatus.OtherWindow, claude.Status);
        claude.IsServedElsewhere = false;

        claude.IsListening = true;
        Assert.Equal(McpStatus.Waiting, claude.Status);

        claude.IsConnected = true;
        Assert.Equal(McpStatus.Connected, claude.Status);

        claude.NoteActivity();
        Assert.Equal(McpStatus.Editing, claude.Status);
        claude.IsActive = false;
        Assert.Equal(McpStatus.Connected, claude.Status);
        claude.IsBusy = true;
        Assert.Equal(McpStatus.Editing, claude.Status);
        claude.IsBusy = false;

        claude.IsConnected = false;
        claude.IsListening = false;
        Assert.Equal(McpStatus.Off, claude.Status);
    }

    [AvaloniaFact]
    public void Every_flag_change_announces_the_status()
    {
        var claude = App.CreateEditor(null).Claude;
        int changes = 0;
        claude.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClaudePanelViewModel.Status))
                changes++;
        };

        claude.IsServedElsewhere = true;
        claude.IsListening = true;
        claude.IsConnected = true;
        claude.IsActive = true;
        claude.IsBusy = true;

        Assert.Equal(5, changes);
    }

    [AvaloniaTheory]
    [InlineData(DesignScreen.Editing, McpStatus.Connected)]
    [InlineData(DesignScreen.Ai, McpStatus.Editing)]
    [InlineData(DesignScreen.SettingsMcp, McpStatus.Connected)]
    public void The_demo_shows_Claude_connected(DesignScreen screen, McpStatus status) =>
        Assert.Equal(status, App.CreateEditor(screen).Claude.Status);
}
