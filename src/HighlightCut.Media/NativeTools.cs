using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HighlightCut.Media;

/// <summary>
/// Locates the native tools HighlightCut depends on. Lookup order: an explicit directory
/// (settings or HIGHLIGHTCUT_FFMPEG_DIR), then the app directory (filled by scripts/fetch-deps.ps1),
/// then PATH.
/// </summary>
public static partial class NativeTools
{
    public const string FfmpegDirEnvironmentVariable = "HIGHLIGHTCUT_FFMPEG_DIR";

    public static string ExecutableName(string tool) =>
        OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    /// <summary>Returns the full path of <paramref name="tool"/> (e.g. "ffprobe"), or null if not found.</summary>
    public static string? FindTool(string tool, string? preferredDirectory = null)
    {
        string file = ExecutableName(tool);
        foreach (string? dir in CandidateDirectories(preferredDirectory))
        {
            if (string.IsNullOrEmpty(dir))
                continue;
            string path = Path.Combine(dir, file);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    /// <summary>
    /// Runs <c>&lt;tool&gt; -version</c> and returns the version, e.g. "7.1" or "n9.0.1-11-ge47273f4d9",
    /// or null if the tool is missing or does not answer.
    /// </summary>
    public static async Task<string?> GetVersionAsync(string tool, string? preferredDirectory = null,
        CancellationToken cancellationToken = default)
    {
        string? path = FindTool(tool, preferredDirectory);
        if (path is null)
            return null;
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-version");
        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return null;
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return ParseVersion(output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Extracts the version from the first line of <c>ffmpeg -version</c> output.</summary>
    public static string? ParseVersion(string output)
    {
        var m = VersionLine().Match(output);
        if (!m.Success)
            return null;
        string v = m.Groups[1].Value;
        // Release builds carry suffixes ("6.1.1-3ubuntu5", "n9.0.1-11-ge47273f4d9"); keep the release number.
        var plain = PlainVersion().Match(v);
        return plain.Success ? plain.Groups[1].Value : v;
    }

    private static IEnumerable<string?> CandidateDirectories(string? preferredDirectory)
    {
        yield return preferredDirectory;
        yield return Environment.GetEnvironmentVariable(FfmpegDirEnvironmentVariable);
        yield return AppContext.BaseDirectory;
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
            yield break;
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return dir;
    }

    [GeneratedRegex(@"^\S+ version (\S+)", RegexOptions.Multiline)]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"^n?(\d+(?:\.\d+)+)")]
    private static partial Regex PlainVersion();
}
