using System.ComponentModel;
using System.Diagnostics;

namespace HighlightCut.App.Services;

/// <summary>How Claude starts HighlightCut's MCP server (<c>HighlightCut mcp</c>), and how that server starts the editor.</summary>
public static class EditorLauncher
{
    /// <summary>The program and arguments that run this HighlightCut in MCP mode, for Claude's settings.</summary>
    public static (string Command, IReadOnlyList<string> Args) McpCommand()
    {
        var (exe, args) = Self();
        return (exe, [.. args, "mcp"]);
    }

    /// <summary>Starts the editor window. Returns false if it could not be started.</summary>
    public static bool Launch()
    {
        try
        {
            var (exe, args) = Self();
            var start = new ProcessStartInfo(exe) { WorkingDirectory = AppContext.BaseDirectory };
            foreach (string arg in args)
                start.ArgumentList.Add(arg);
            if (OperatingSystem.IsWindows())
            {
                // Through the shell, so the editor does not inherit the pipes Claude talks to this process on.
                start.UseShellExecute = true;
                using var process = Process.Start(start);
                return process is not null;
            }
            // Elsewhere: give the editor its own stdio and close it, for the same reason.
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using (var process = Process.Start(start))
            {
                if (process is null)
                    return false;
                process.StandardInput.Close();
                process.StandardOutput.Close();
                process.StandardError.Close();
            }
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>This program: HighlightCut(.exe), or <c>dotnet HighlightCut.dll</c> when run through the dotnet host.</summary>
    private static (string Exe, string[] Args) Self()
    {
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "HighlightCut.exe" : "HighlightCut");
        return Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? (exe, [typeof(EditorLauncher).Assembly.Location])
            : (exe, []);
    }
}
