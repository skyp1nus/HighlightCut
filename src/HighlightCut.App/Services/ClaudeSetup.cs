using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace HighlightCut.App.Services;

/// <summary>The system the setup runs on: it decides the script, the terminal and where the <c>claude</c> CLI may be.</summary>
public enum SetupPlatform
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>A process that ran to the end (or was stopped after the timeout), with its output and errors together.</summary>
public sealed record ProcessOutput(int ExitCode, string Output, bool TimedOut = false);

/// <summary>Starts processes for <see cref="ClaudeSetup"/>; tests replace it so no terminal or <c>claude</c> is started.</summary>
public interface IProcessRunner
{
    /// <summary>Starts a program and leaves it running (a terminal window). False if it could not be started.</summary>
    bool Start(ProcessStartInfo start);

    /// <summary>Runs a hidden program to the end; stops it after <paramref name="timeout"/>.</summary>
    Task<ProcessOutput> RunAsync(ProcessStartInfo start, TimeSpan timeout);
}

/// <summary>Starts real processes.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public bool Start(ProcessStartInfo start)
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

    public async Task<ProcessOutput> RunAsync(ProcessStartInfo start, TimeSpan timeout)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The process did not start.");
        if (start.RedirectStandardInput)
            process.StandardInput.Close();
        var output = start.RedirectStandardOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var errors = start.RedirectStandardError ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                // It ended just now, or can't be stopped; either way its result is not waited for.
            }
            return new ProcessOutput(-1, "", TimedOut: true);
        }
        string text = (await errors.ConfigureAwait(false)) + "\n" + (await output.ConfigureAwait(false));
        return new ProcessOutput(process.ExitCode, text.Trim());
    }
}

/// <summary>A <c>claude</c> command: its arguments, and whether its failure stops the setup.</summary>
public sealed record ClaudeCommand(IReadOnlyList<string> Args, bool MayFail);

/// <summary>What adding HighlightCut to Claude Code did.</summary>
public enum ClaudeCodeOutcome
{
    Added,
    Failed,
    TimedOut,
    NotStarted,
}

public sealed record ClaudeCodeResult(ClaudeCodeOutcome Outcome, string Message = "");

/// <summary>
/// Adds HighlightCut to Claude Code: finds the <c>claude</c> CLI, then runs <c>claude mcp remove</c> and
/// <c>claude mcp add</c> hidden (<see cref="AddToClaudeCodeAsync"/>) or in a terminal window (<see cref="OpenTerminal"/>).
/// The terminal runs a script file written to the temp folder, so no command line goes through Windows Terminal's
/// own parsing (it splits at <c>;</c>) or cmd's quote rules.
/// </summary>
public sealed partial class ClaudeSetup(SetupPlatform platform, Func<string, string?> environment, Func<string, bool> fileExists, IProcessRunner runner)
{
    public const string InstallPage = "https://code.claude.com/docs/en/setup";

    /// <summary>How long one hidden <c>claude</c> command may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static SetupPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? SetupPlatform.Windows : OperatingSystem.IsMacOS() ? SetupPlatform.MacOS : SetupPlatform.Linux;

    /// <summary>The setup for this computer.</summary>
    public static ClaudeSetup ForThisSystem() => new(CurrentPlatform, Environment.GetEnvironmentVariable, File.Exists, new SystemProcessRunner());

    public SetupPlatform Platform { get; } = platform;

    /// <summary>Where the terminal scripts are written: <c>%TEMP%\HighlightCut</c>.</summary>
    public string ScriptFolder { get; init; } = Path.Combine(Path.GetTempPath(), "HighlightCut");

    private bool IsWindows => Platform == SetupPlatform.Windows;

    // ---- Finding claude -----------------------------------------------------------------------

    /// <summary>
    /// The <c>claude</c> program: on the PATH (with PATHEXT's .exe/.cmd/.bat/.com on Windows), or where its installers put
    /// it. Null if it is not installed, or somewhere else.
    /// </summary>
    public string? FindClaude()
    {
        char separator = IsWindows ? ';' : ':';
        var folders = (environment("PATH") ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.Trim('"')).ToList();
        if (IsWindows)
        {
            if (environment("USERPROFILE") is { Length: > 0 } profile)
                folders.Add(Join(profile, ".local", "bin"));
            if (environment("APPDATA") is { Length: > 0 } appData)
                folders.Add(Join(appData, "npm"));
        }
        else
        {
            if (environment("HOME") is { Length: > 0 } home)
            {
                folders.Add(Join(home, ".local", "bin"));
                folders.Add(Join(home, ".claude", "local"));
            }
            folders.Add("/usr/local/bin");
            folders.Add("/opt/homebrew/bin");
        }

        string[] names = IsWindows ? [.. WindowsExtensions().Select(ext => "claude" + ext)] : ["claude"];
        foreach (string folder in folders)
        {
            foreach (string name in names)
            {
                string path = Join(folder, name);
                if (fileExists(path))
                    return path;
            }
        }
        return null;
    }

    // The PATHEXT extensions a program can be run with here: .exe/.com directly, .cmd/.bat through cmd.
    private IEnumerable<string> WindowsExtensions()
    {
        string pathExt = environment("PATHEXT") is { Length: > 0 } value ? value : ".COM;.EXE;.BAT;.CMD";
        return pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant())
            .Where(e => e is ".exe" or ".com" or ".cmd" or ".bat")
            .Distinct();
    }

    // Paths of the platform the setup is for, not the one it runs on (tests make Windows paths on Linux).
    private char Separator => IsWindows ? '\\' : '/';

    private string Join(params string[] parts) =>
        string.Join(Separator, parts.Select((p, i) => i == 0 ? p.TrimEnd('\\', '/') : p.Trim('\\', '/')));

    private string FolderOf(string path)
    {
        int end = IsWindows ? path.LastIndexOfAny(['\\', '/']) : path.LastIndexOf('/');
        return end > 0 ? path[..end] : "";
    }

    private string NameOf(string path)
    {
        string folder = FolderOf(path);
        return folder.Length > 0 ? path[(folder.Length + 1)..] : path;
    }

    private static bool IsBatch(string program) =>
        program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    // ---- The commands -------------------------------------------------------------------------

    /// <summary>
    /// Remove the old entries (their failure is fine: they may not be there), then add HighlightCut for every project.
    /// Removing first means that adding again always replaces the path, after the app moved say.
    /// </summary>
    public static IReadOnlyList<ClaudeCommand> Commands(string program, IReadOnlyList<string> args, bool removeOurCut)
    {
        var commands = new List<ClaudeCommand>();
        if (removeOurCut)
            commands.Add(new(["mcp", "remove", "--scope", "user", "ourcut"], MayFail: true));
        commands.Add(new(["mcp", "remove", "--scope", "user", "highlightcut"], MayFail: true));
        commands.Add(new(["mcp", "add", "--scope", "user", "highlightcut", "--", program, .. args], MayFail: false));
        return commands;
    }

    /// <summary>Runs the commands hidden, one after another, and says how the last one (the add) went.</summary>
    public async Task<ClaudeCodeResult> AddToClaudeCodeAsync(string claude, IReadOnlyList<ClaudeCommand> commands)
    {
        foreach (var command in commands)
        {
            ProcessOutput result;
            try
            {
                result = await runner.RunAsync(HiddenStart(claude, command.Args), Timeout).ConfigureAwait(false);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                return new(ClaudeCodeOutcome.NotStarted, e.Message);
            }
            if (result.TimedOut)
                return new(ClaudeCodeOutcome.TimedOut);
            if (result.ExitCode != 0 && !command.MayFail)
                return new(ClaudeCodeOutcome.Failed, Summary(result.Output));
        }
        return new(ClaudeCodeOutcome.Added);
    }

    /// <summary>How a hidden <c>claude</c> command is started: no window, output captured, .cmd shims through <c>cmd /c</c>.</summary>
    public ProcessStartInfo HiddenStart(string claude, IReadOnlyList<string> args)
    {
        ProcessStartInfo start;
        if (IsWindows && IsBatch(claude))
        {
            // cmd /s /c "…": cmd drops the outer quotes and runs the rest as written.
            string line = string.Join(' ', args.Prepend(claude).Select(a => QuoteWindows(a, script: false)));
            start = new ProcessStartInfo(environment("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe", "/d /s /c \"" + line + "\"");
        }
        else
        {
            start = new ProcessStartInfo(claude);
            foreach (string arg in args)
                start.ArgumentList.Add(arg);
        }
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        // An npm install of claude needs node, which is usually next to it but not always on a GUI app's PATH (macOS).
        if (FolderOf(claude) is { Length: > 0 } folder)
            start.Environment["PATH"] = folder + (IsWindows ? ";" : ":") + environment("PATH");
        return start;
    }

    // The last lines Claude Code printed, without colours: what went wrong, for the step's note.
    private static string Summary(string output)
    {
        var lines = Ansi().Replace(output, "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string text = string.Join(' ', lines.TakeLast(2));
        return text.Length > 240 ? text[..239] + "…" : text;
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();

    // ---- In a terminal ------------------------------------------------------------------------

    /// <summary>
    /// Writes the commands to a script in <see cref="ScriptFolder"/> and runs it in a new terminal window that stays
    /// open, so its result can be read. False if the script could not be written or no terminal could be started.
    /// </summary>
    public bool OpenTerminal(string claude, IReadOnlyList<ClaudeCommand> commands)
    {
        string file;
        try
        {
            Directory.CreateDirectory(ScriptFolder);
            DeleteOldScripts();
            file = Path.Combine(ScriptFolder, $"add-to-claude-code-{Guid.NewGuid():N}{(IsWindows ? ".cmd" : Platform == SetupPlatform.MacOS ? ".command" : ".sh")}");
            // UTF-8 without a BOM: cmd would read a BOM as part of the first command.
            File.WriteAllText(file, IsWindows ? WindowsScript(claude, commands) : UnixScript(claude, commands), new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return TerminalStarts(file).Any(runner.Start);
    }

    // Scripts from earlier runs, once their terminal has surely read them.
    private void DeleteOldScripts()
    {
        foreach (string old in Directory.EnumerateFiles(ScriptFolder, "add-to-claude-code-*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddHours(-1))
                    File.Delete(old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // In use or not ours; it is only a small file in the temp folder.
            }
        }
    }

    /// <summary>
    /// The ways to open a terminal on the script, tried in order. On Windows the terminal starts in the script's folder and
    /// runs it by its plain file name, so neither Windows Terminal nor cmd has a path with spaces or brackets to quote.
    /// </summary>
    public IReadOnlyList<ProcessStartInfo> TerminalStarts(string script)
    {
        string folder = FolderOf(script);
        string name = NameOf(script);
        switch (Platform)
        {
            case SetupPlatform.Windows:
                // wt splits its command line at ';' (not at "\;"), even inside quotes.
                return
                [
                    Start("wt.exe", ["new-tab", "--title", "HighlightCut", "-d", folder.Replace(";", "\\;", StringComparison.Ordinal), "cmd.exe", "/k", name], folder, shell: true),
                    Start("cmd.exe", ["/k", name], folder, shell: true),
                ];
            case SetupPlatform.MacOS:
                return [Start("open", ["-a", "Terminal", script], folder)];
            default:
                return
                [
                    Start("x-terminal-emulator", ["-e", "sh", script], folder),
                    Start("gnome-terminal", ["--", "sh", script], folder),
                    Start("konsole", ["-e", "sh", script], folder),
                    Start("xterm", ["-e", "sh", script], folder),
                ];
        }
    }

    private static ProcessStartInfo Start(string program, string[] args, string folder, bool shell = false)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = shell, WorkingDirectory = folder };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        return start;
    }

    /// <summary>The Windows script: says what it does, runs the commands and ends with a line saying whether it worked.</summary>
    public static string WindowsScript(string claude, IReadOnlyList<ClaudeCommand> commands)
    {
        // call: a .cmd shim would otherwise end this script when it ends.
        string run = (IsBatch(claude) ? "call " : "") + QuoteWindows(claude, script: true);
        var s = new StringBuilder();
        s.Append("@echo off\r\n");
        s.Append("chcp 65001 >nul\r\n");
        s.Append("title HighlightCut\r\n");
        s.Append("echo Adding HighlightCut to Claude Code.\r\n");
        s.Append("echo.\r\n");
        foreach (var command in commands)
        {
            s.Append("echo ").Append(EchoWindows(Describe(command))).Append("\r\n");
            string line = run + " " + string.Join(' ', command.Args.Select(a => QuoteWindows(a, script: true)));
            s.Append(line).Append(command.MayFail ? " >nul 2>&1\r\n" : "\r\n");
        }
        s.Append("if errorlevel 1 (\r\n");
        s.Append("  echo.\r\n");
        s.Append("  echo FAILED: HighlightCut was not added to Claude Code. See the message above.\r\n");
        s.Append(") else (\r\n");
        s.Append("  echo.\r\n");
        s.Append("  echo DONE: HighlightCut is added to Claude Code. Start a new Claude Code chat to use it.\r\n");
        s.Append(")\r\n");
        return s.ToString();
    }

    /// <summary>The macOS and Linux script, the same steps for sh.</summary>
    public static string UnixScript(string claude, IReadOnlyList<ClaudeCommand> commands)
    {
        var s = new StringBuilder();
        s.Append("#!/bin/sh\n");
        s.Append("echo 'Adding HighlightCut to Claude Code.'\n");
        s.Append("echo\n");
        string run = QuoteUnix(claude);
        for (int i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            s.Append("echo ").Append(QuoteUnix(Describe(command))).Append('\n');
            string line = run + " " + string.Join(' ', command.Args.Select(QuoteUnix));
            if (command.MayFail)
            {
                s.Append(line).Append(" >/dev/null 2>&1\n");
                continue;
            }
            s.Append("if ").Append(line).Append("; then\n");
            s.Append("  echo\n  echo 'DONE: HighlightCut is added to Claude Code. Start a new Claude Code chat to use it.'\n");
            s.Append("else\n");
            s.Append("  echo\n  echo 'FAILED: HighlightCut was not added to Claude Code. See the message above.'\n");
            s.Append("fi\n");
        }
        s.Append("echo\n");
        s.Append("printf 'Press Enter to close this window. '\n");
        s.Append("read -r _\n");
        return s.ToString();
    }

    private static string Describe(ClaudeCommand command) => command.Args switch
    {
        [_, "remove", .., "ourcut"] => "Removing the old OurCut entry, if there is one...",
        [_, "remove", ..] => "Removing an earlier HighlightCut entry, if there is one...",
        _ => "Adding HighlightCut...",
    };

    /// <summary>A cmd argument: quoted when it has spaces or cmd's special characters, with <c>%</c> doubled in a script.</summary>
    internal static string QuoteWindows(string arg, bool script)
    {
        string text = script ? arg.Replace("%", "%%", StringComparison.Ordinal) : arg;
        return text.Length > 0 && !text.Any(ch => char.IsWhiteSpace(ch) || ch is '&' or '|' or '<' or '>' or '^' or '(' or ')' or ',' or ';' or '=' or '"')
            ? text
            : '"' + text + '"';
    }

    private static string EchoWindows(string text)
    {
        var s = new StringBuilder();
        foreach (char ch in text)
        {
            if (ch is '&' or '|' or '<' or '>' or '^' or '(' or ')')
                s.Append('^');
            s.Append(ch);
        }
        return s.ToString();
    }

    internal static string QuoteUnix(string arg) =>
        arg.Length > 0 && arg.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '/' or ':' or '=' or '+' or ',')
            ? arg
            : "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
