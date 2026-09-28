using Avalonia;
using HighlightCut.App.Services;
using HighlightCut.Mcp;

namespace HighlightCut.App;

internal static class Program
{
    // Don't use Avalonia or anything that needs a SynchronizationContext before AppMain runs.
    [STAThread]
    public static int Main(string[] args)
    {
        // "HighlightCut mcp": the MCP server Claude starts over stdio. It has no window; it forwards Claude's
        // tool calls to the editor (starting it when needed). Nothing else may write to stdout here.
        if (args is ["mcp", ..])
            return McpBridge.RunStdioAsync(EditorLauncher.Launch).GetAwaiter().GetResult();
        MoveLegacyAppData();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Settings, models, the cache and logs from before the rename (%LOCALAPPDATA%\OurCut). Runs before anything reads
    // them; whatever cannot be moved is still read from the old folder.
    private static void MoveLegacyAppData()
    {
        var result = AppDataFolder.MoveLegacy();
        if (!result.Found)
            return;
        CrashLog.Note($"Moved the app folder from {AppDataFolder.LegacyName} to {AppDataFolder.Name}", string.Join(Environment.NewLine,
            [.. result.Moved.Select(p => "Moved: " + p), .. result.Kept.Select(p => "Kept (already in the new folder): " + p),
             .. result.Failed.Select(p => "Could not move (still used from there): " + p)]));
    }

    // Also used by the IDE previewer and the headless UI tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithHighlightCutFonts()
            .LogToTrace();
}
