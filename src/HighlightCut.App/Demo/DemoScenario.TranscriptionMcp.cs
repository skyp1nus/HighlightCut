using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Demo;

public static partial class DemoScenario
{
    /// <summary>Settings → Transcription and MCP server as the design shows them: Claude Desktop connected 12 minutes ago.</summary>
    static partial void ApplyTranscriptionMcp(EditorViewModel editor, DesignScreen screen)
    {
        var claude = editor.Claude;
        claude.IsListening = true;
        claude.ClientName = DesignSettingsSample.ClientName;
        claude.ConnectedSince = DateTimeOffset.Now - DesignSettingsSample.ConnectedFor;
        claude.OtherWindowProject = DesignSettingsSample.OtherWindowProject;
        editor.Settings.LoadDesignMcp();
    }
}
