using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace HighlightCut.Media.Tools;

/// <summary>ffmpeg or ffprobe exited with an error.</summary>
public sealed partial class MediaToolException : Exception
{
    public MediaToolException()
    {
    }

    public MediaToolException(string message)
        : base(message)
    {
    }

    public MediaToolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MediaToolException(string tool, int exitCode, string errorOutput)
        : base(Describe(tool, exitCode, errorOutput))
    {
        Tool = tool;
        ExitCode = exitCode;
        ErrorOutput = errorOutput;
    }

    public string? Tool { get; }
    public int ExitCode { get; }

    /// <summary>The last lines the tool wrote to stderr.</summary>
    public string? ErrorOutput { get; }

    /// <summary>ffmpeg's closing lines, which only say that something above went wrong.</summary>
    private static readonly string[] GenericEndings =
    [
        "Conversion failed!", "Error opening output file", "Error opening input file", "Error parsing options for",
        "Failed to set value", "Error selecting an encoder", "Error initializing output stream", "Error while opening encoder",
        "To ignore this, add a trailing '?'", "Exiting normally", "Terminating thread",
    ];

    /// <summary>
    /// The last line of the error output that says what went wrong, without the "[in#0 @ 0x…]" prefix,
    /// e.g. "Stream map '0:9' matches no streams." rather than "Error opening output files: Invalid argument".
    /// </summary>
    internal static string Describe(string tool, int exitCode, string errorOutput)
    {
        var lines = errorOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => LogPrefix().Replace(l, "")).Where(l => l.Length > 0).ToList();
        string? line = lines.LastOrDefault(l => !GenericEndings.Any(g => l.StartsWith(g, StringComparison.Ordinal))) ?? lines.LastOrDefault();
        return line is not null ? $"{tool} failed: {line}" : $"{tool} failed with exit code {exitCode}.";
    }

    [GeneratedRegex(@"^\[[^\]]+ @ (0x)?[0-9a-fA-F]+\]\s*")]
    private static partial Regex LogPrefix();
}

/// <summary>
/// Runs ffmpeg/ffprobe without a console window, streaming stdout to a callback. Stdout is read to the
/// end before the exit code is checked, so no output is lost; cancelling kills the process tree.
/// </summary>
public static class ToolProcess
{
    private const int ErrorLinesKept = 30;

    /// <param name="tool">"ffmpeg" or "ffprobe" (resolved with <see cref="NativeTools"/>) or a full path.</param>
    /// <param name="readOutput">Consumes stdout; null discards it.</param>
    /// <exception cref="MediaToolException">The tool is missing or exits with an error.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the process has been killed.</exception>
    public static async Task RunAsync(string tool, IEnumerable<string> arguments,
        Func<Stream, CancellationToken, Task>? readOutput, CancellationToken cancellationToken)
    {
        string path = Path.IsPathFullyQualified(tool) ? tool : NativeTools.FindTool(tool)
            ?? throw new MediaToolException($"{tool} was not found. Run scripts/fetch-deps.ps1 or install FFmpeg.");
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in arguments)
            info.ArgumentList.Add(a);

        using var process = new Process { StartInfo = info };
        var errors = new Queue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (errors)
            {
                errors.Enqueue(e.Data);
                if (errors.Count > ErrorLinesKept)
                    errors.Dequeue();
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new MediaToolException($"Could not start {tool}: {e.Message}", e);
        }
        process.BeginErrorReadLine();
        YieldToPlayback(process);

        using var kill = cancellationToken.Register(() => Kill(process));
        try
        {
            var stdout = process.StandardOutput.BaseStream;
            if (readOutput is not null)
                await readOutput(stdout, cancellationToken).ConfigureAwait(false);
            // Drain whatever the consumer did not read so the process never blocks on a full pipe.
            await stdout.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            Kill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            string tail;
            lock (errors)
                tail = string.Join('\n', errors);
            throw new MediaToolException(Path.GetFileNameWithoutExtension(path), process.ExitCode, tail);
        }
    }

    /// <summary>Runs a tool and returns its whole stdout as text.</summary>
    public static async Task<string> ReadAllTextAsync(string tool, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        string text = "";
        await RunAsync(tool, arguments, async (stream, ct) =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return text;
    }

    /// <summary>Runs a tool and calls <paramref name="onLine"/> for each stdout line as it arrives.</summary>
    public static Task ReadLinesAsync(string tool, IEnumerable<string> arguments, Action<string> onLine, CancellationToken cancellationToken) =>
        RunAsync(tool, arguments, async (stream, ct) =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                onLine(line);
        }, cancellationToken);

    /// <summary>
    /// Analysis (keyframes, waveform, thumbnails, scenes, speech audio) runs beside playback and the UI and gets the CPU
    /// they leave; exports run through FFMpegCore at normal priority.
    /// </summary>
    private static void YieldToPlayback(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Already exited, or the system does not allow it: it runs at normal priority.
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}
