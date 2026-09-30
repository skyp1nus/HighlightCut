using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace HighlightCut.App.Services;

/// <summary>What <see cref="ClaudeSetup.PrepareDesktopConfig"/> found in Claude Desktop's settings file.</summary>
public enum DesktopConfigState
{
    /// <summary>There was no file; it was written with HighlightCut in it.</summary>
    Created,

    /// <summary>The file exists without HighlightCut; the entry has to be pasted in.</summary>
    NeedsEntry,

    /// <summary>The file already has a <c>highlightcut</c> server.</summary>
    AlreadyAdded,

    /// <summary>The file could not be read or written.</summary>
    Failed,
}

/// <summary>The welcome tour's "Open terminal" and "Open config file": adding HighlightCut to Claude Code or Claude Desktop.</summary>
public static class ClaudeSetup
{
    /// <summary>
    /// Runs <paramref name="command"/> in a new terminal window that stays open afterwards, so its result can be read:
    /// Windows Terminal (or cmd), Terminal on macOS, the desktop's terminal on Linux. False if none could be started.
    /// </summary>
    public static bool RunInTerminal(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            // The command is passed as typed: cmd /k runs the rest of the line (quotes included) and keeps the window.
            return TryStart(new ProcessStartInfo("wt.exe", "new-tab cmd.exe /k " + command) { UseShellExecute = true })
                || TryStart(new ProcessStartInfo("cmd.exe", "/k " + command) { UseShellExecute = true });
        }
        if (OperatingSystem.IsMacOS())
        {
            string script = "tell application \"Terminal\"\nactivate\ndo script \""
                + command.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"\nend tell";
            return TryStart(new ProcessStartInfo("osascript") { ArgumentList = { "-e", script }, UseShellExecute = false });
        }
        // The shell stays after the command, like cmd /k.
        string shell = command + "; exec \"${SHELL:-sh}\"";
        (string Program, string[] Args)[] terminals =
        [
            ("x-terminal-emulator", ["-e", "sh", "-c", shell]),
            ("gnome-terminal", ["--", "sh", "-c", shell]),
            ("konsole", ["-e", "sh", "-c", shell]),
            ("xterm", ["-e", "sh", "-c", shell]),
        ];
        foreach (var (program, args) in terminals)
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (string arg in args)
                start.ArgumentList.Add(arg);
            if (TryStart(start))
                return true;
        }
        return false;
    }

    private static bool TryStart(ProcessStartInfo start)
    {
        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets Claude Desktop's settings file ready to open: writes it with <paramref name="config"/> (the whole
    /// <c>{"mcpServers": …}</c> entry) when there is none, and otherwise only says whether HighlightCut is in it.
    /// </summary>
    public static DesktopConfigState PrepareDesktopConfig(string file, string config)
    {
        try
        {
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, config + "\n");
                return DesktopConfigState.Created;
            }
            return HasHighlightCut(File.ReadAllText(file)) ? DesktopConfigState.AlreadyAdded : DesktopConfigState.NeedsEntry;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DesktopConfigState.Failed;
        }
    }

    /// <summary>The JSON has a <c>highlightcut</c> server under <c>mcpServers</c>; false for anything it cannot read.</summary>
    public static bool HasHighlightCut(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("mcpServers", out var servers)
                && servers.ValueKind == JsonValueKind.Object
                && servers.EnumerateObject().Any(s => s.Name.Equals("highlightcut", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
