using Avalonia;
using HighlightCut.App.Services;
using HighlightCut.Mcp;
using HighlightCut.Transcription;
using HighlightCut.Transcription.Models;

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
        // The DirectML check, in a process of its own: a GPU driver that fails ends this one, not the editor.
        if (args is ["--probe-gpu-child", .. var check])
            return GpuProbe.RunChild(check);
        GpuProbe.Command = (Environment.ProcessPath!, ["--probe-gpu-child"]);
        // "HighlightCut --probe-gpu <model id> <model folder>": runs the check now (even without a GPU) and prints
        // what it found.
        if (args is ["--probe-gpu", .. var model])
            return ProbeGpu(model);
        MoveLegacyAppData();
        GpuProbe.CacheFile = AppDataFolder.PathFor("gpu-check.json");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static int ProbeGpu(string[] args)
    {
        if (args.Length < 2 || ModelCatalog.Find(args[0]) is not { } model)
        {
            Console.Error.WriteLine("usage: --probe-gpu <model id> <model folder>");
            return 2;
        }
        Console.WriteLine($"GPU: {GpuAdapter.Current?.ToString() ?? "none"}");
        Console.WriteLine($"Runtime: {RecognizerPlan.InstalledGpuProvider ?? "CPU only"}");
        var result = GpuProbe.Run(model, args[1], ModelCatalog.OnlyLanguage(model));
        Console.WriteLine($"Check: {result}");
        var plan = RecognizerPlan.Choose(TranscriptionDevice.Auto, Environment.ProcessorCount, RecognizerPlan.InstalledGpuProvider, result);
        Console.WriteLine($"Auto uses: {plan.Description}");
        return 0;
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
