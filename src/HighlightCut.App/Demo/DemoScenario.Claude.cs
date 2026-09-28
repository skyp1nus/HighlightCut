using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Demo;

/// <summary>The Claude export screens: the request, the export running, and the failed export (prototype <c>claudeReq</c>, <c>claudeExp</c>, <c>claudeFail</c>).</summary>
public static partial class DemoScenario
{
    internal const string DemoExportError =
        "Can’t write D:\\Videos\\keynote-cut.mp4: the file is open in another program. Close it there, then retry.";

    /// <summary>What Claude asks to write in the design (REQ).</summary>
    internal static ClaudeExportTarget DemoExportTarget(EditorViewModel editor) => editor.Export.ClaudeTarget() with
    {
        Path = @"D:\Videos\keynote-cut.mp4",
        Name = "keynote-cut.mp4",
        Folder = @"D:\Videos",
        Mode = "Lossless",
        Container = "MP4",
    };

    static partial void ApplyClaude(EditorViewModel editor, DesignScreen screen)
    {
        if (screen is not (DesignScreen.ClaudeRequest or DesignScreen.ClaudeExporting or DesignScreen.ClaudeExportFailed))
            return;
        editor.Select(null);
        editor.Claude.IsOpen = true;
        var target = DemoExportTarget(editor);
        switch (screen)
        {
            case DesignScreen.ClaudeRequest:
                _ = AnswerDemoRequestAsync(editor, target);
                break;
            case DesignScreen.ClaudeExporting:
                editor.Claude.Export.Expect(target);
                editor.Export.Start(0.45, byClaude: true);
                break;
            case DesignScreen.ClaudeExportFailed:
                editor.Claude.Export.ShowFailed(target, DemoExportError, DateTimeOffset.Now.AddMinutes(-1));
                break;
        }
    }

    /// <summary>Allow in the demo runs the simulated export, as the prototype's claudeStart does.</summary>
    private static async Task AnswerDemoRequestAsync(EditorViewModel editor, ClaudeExportTarget target)
    {
        try
        {
            if (await editor.Claude.Export.AskAsync(target).ConfigureAwait(true) != ClaudeExportAnswer.Deny && editor.IsDemo)
                editor.Export.Start(0, byClaude: true);
        }
        catch (OperationCanceledException)
        {
            // Another screen or file replaced the request.
        }
    }
}
