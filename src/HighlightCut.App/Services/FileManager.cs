using System.ComponentModel;
using System.Diagnostics;

namespace HighlightCut.App.Services;

/// <summary>Opens the system file manager.</summary>
public static class FileManager
{
    /// <summary>Shows a file selected in Explorer (Finder, or the folder on Linux).</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path }, UseShellExecute = false })?.Dispose();
            }
            else
            {
                string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { folder }, UseShellExecute = false })?.Dispose();
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            // No file manager available; nothing useful to do.
        }
    }

    /// <summary>Opens a file in its default app.</summary>
    public static void Open(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            else
                Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { ArgumentList = { path }, UseShellExecute = false })?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            // No app for the file; nothing useful to do.
        }
    }
}
