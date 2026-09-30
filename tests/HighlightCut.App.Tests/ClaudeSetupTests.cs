using System.ComponentModel;
using System.Diagnostics;
using HighlightCut.App.Services;

namespace HighlightCut.App.Tests;

/// <summary>Records what <see cref="ClaudeSetup"/> starts and answers with scripted results; nothing is really started.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessStartInfo> Started { get; } = [];
    public List<ProcessStartInfo> Ran { get; } = [];

    /// <summary>Whether each Start succeeds, in order; true once they run out.</summary>
    public Queue<bool> StartResults { get; } = new();

    /// <summary>The results of RunAsync, in order; exit code 0 once they run out.</summary>
    public Queue<Func<ProcessOutput>> RunResults { get; } = new();

    public bool Start(ProcessStartInfo start)
    {
        Started.Add(start);
        return StartResults.Count == 0 || StartResults.Dequeue();
    }

    public Task<ProcessOutput> RunAsync(ProcessStartInfo start, TimeSpan timeout)
    {
        Ran.Add(start);
        return Task.FromResult(RunResults.Count > 0 ? RunResults.Dequeue()() : new ProcessOutput(0, ""));
    }
}

/// <summary>Adding HighlightCut to Claude Code: finding claude, the commands, the terminal scripts and how they start.</summary>
public sealed class ClaudeSetupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-claude-setup").FullName;
    private readonly FakeProcessRunner _runner = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Exe = @"C:\Program Files\HighlightCut\HighlightCut.exe";
    private const string Claude = @"C:\Users\Анна Ko\AppData\Roaming\npm\claude.cmd";

    private ClaudeSetup Setup(SetupPlatform platform, Dictionary<string, string>? env = null, params string[] files) =>
        new(platform, name => env?.GetValueOrDefault(name), files.Contains, _runner) { ScriptFolder = Path.Combine(_dir, "scripts") };

    // ---- Finding claude -----------------------------------------------------------------------

    [Fact]
    public void Claude_is_found_on_the_path_with_pathext_on_windows()
    {
        var env = new Dictionary<string, string>
        {
            ["PATH"] = @"C:\Windows\system32;""C:\Tools\"";C:\Users\a\AppData\Roaming\npm",
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD;.VBS;.PS1",
        };
        // npm puts a sh script and a .ps1 next to claude.cmd: only the .cmd can be run.
        var setup = Setup(SetupPlatform.Windows, env,
            @"C:\Users\a\AppData\Roaming\npm\claude", @"C:\Users\a\AppData\Roaming\npm\claude.ps1", @"C:\Users\a\AppData\Roaming\npm\claude.cmd");
        Assert.Equal(@"C:\Users\a\AppData\Roaming\npm\claude.cmd", setup.FindClaude());

        // The first folder on the PATH wins, and a quoted folder with a trailing backslash works.
        setup = Setup(SetupPlatform.Windows, env, @"C:\Tools\claude.exe", @"C:\Users\a\AppData\Roaming\npm\claude.cmd");
        Assert.Equal(@"C:\Tools\claude.exe", setup.FindClaude());
    }

    [Fact]
    public void Claude_is_found_where_its_installers_put_it_when_not_on_the_path()
    {
        var env = new Dictionary<string, string>
        {
            ["PATH"] = @"C:\Windows\system32",
            ["USERPROFILE"] = @"C:\Users\a",
            ["APPDATA"] = @"C:\Users\a\AppData\Roaming",
        };
        Assert.Equal(@"C:\Users\a\.local\bin\claude.exe", Setup(SetupPlatform.Windows, env, @"C:\Users\a\.local\bin\claude.exe").FindClaude());
        Assert.Equal(@"C:\Users\a\AppData\Roaming\npm\claude.cmd",
            Setup(SetupPlatform.Windows, env, @"C:\Users\a\AppData\Roaming\npm\claude.cmd").FindClaude());
        Assert.Null(Setup(SetupPlatform.Windows, env).FindClaude());

        var home = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["HOME"] = "/home/a" };
        Assert.Equal("/home/a/.local/bin/claude", Setup(SetupPlatform.Linux, home, "/home/a/.local/bin/claude").FindClaude());
        Assert.Equal("/opt/homebrew/bin/claude", Setup(SetupPlatform.MacOS, home, "/opt/homebrew/bin/claude").FindClaude());
        Assert.Equal("/usr/local/bin/claude", Setup(SetupPlatform.MacOS, home, "/usr/local/bin/claude").FindClaude());
        Assert.Equal("/usr/bin/claude", Setup(SetupPlatform.Linux, home, "/usr/bin/claude").FindClaude());
        Assert.Null(Setup(SetupPlatform.Linux, home).FindClaude());
    }

    // ---- The commands -------------------------------------------------------------------------

    [Fact]
    public void Adding_removes_the_old_entries_first()
    {
        var commands = ClaudeSetup.Commands(Exe, ["mcp"], removeOurCut: true);
        Assert.Equal(
        [
            "mcp remove --scope user ourcut",
            "mcp remove --scope user highlightcut",
            $"mcp add --scope user highlightcut -- {Exe} mcp",
        ], commands.Select(c => string.Join(' ', c.Args)));
        Assert.Equal([true, true, false], commands.Select(c => c.MayFail));

        Assert.Equal(2, ClaudeSetup.Commands(Exe, ["mcp"], removeOurCut: false).Count);
    }

    [Fact]
    public async Task Claude_code_is_run_hidden_and_a_failed_remove_does_not_matter()
    {
        var setup = Setup(SetupPlatform.Linux, new() { ["PATH"] = "/usr/bin" });
        _runner.RunResults.Enqueue(() => new ProcessOutput(1, "No MCP server found with name: ourcut"));
        _runner.RunResults.Enqueue(() => new ProcessOutput(1, "No MCP server found with name: highlightcut"));
        _runner.RunResults.Enqueue(() => new ProcessOutput(0, "Added stdio MCP server highlightcut"));

        var result = await setup.AddToClaudeCodeAsync("/home/a/.local/bin/claude", ClaudeSetup.Commands("/opt/hc/HighlightCut", ["mcp"], true));

        Assert.Equal(ClaudeCodeOutcome.Added, result.Outcome);
        Assert.Equal(3, _runner.Ran.Count);
        var add = _runner.Ran[2];
        Assert.Equal("/home/a/.local/bin/claude", add.FileName);
        Assert.Equal(["mcp", "add", "--scope", "user", "highlightcut", "--", "/opt/hc/HighlightCut", "mcp"], add.ArgumentList);
        Assert.False(add.UseShellExecute);
        Assert.True(add.CreateNoWindow);
        Assert.True(add.RedirectStandardOutput && add.RedirectStandardError && add.RedirectStandardInput);
        // node is usually next to an npm-installed claude.
        Assert.Equal("/home/a/.local/bin:/usr/bin", add.Environment["PATH"]);
    }

    [Fact]
    public async Task A_failed_add_says_what_claude_code_printed()
    {
        var setup = Setup(SetupPlatform.Linux);
        _runner.RunResults.Enqueue(() => new ProcessOutput(0, ""));
        _runner.RunResults.Enqueue(() => new ProcessOutput(1, "\u001b[31mError:\u001b[0m\n  Invalid scope: users\n"));
        var result = await setup.AddToClaudeCodeAsync("claude", ClaudeSetup.Commands("hc", ["mcp"], false));
        Assert.Equal(new ClaudeCodeResult(ClaudeCodeOutcome.Failed, "Error: Invalid scope: users"), result);

        _runner.RunResults.Enqueue(() => new ProcessOutput(0, ""));
        _runner.RunResults.Enqueue(() => new ProcessOutput(-1, "", TimedOut: true));
        Assert.Equal(ClaudeCodeOutcome.TimedOut, (await setup.AddToClaudeCodeAsync("claude", ClaudeSetup.Commands("hc", ["mcp"], false))).Outcome);

        _runner.RunResults.Enqueue(() => throw new Win32Exception("The system cannot find the file specified"));
        result = await setup.AddToClaudeCodeAsync("claude", ClaudeSetup.Commands("hc", ["mcp"], false));
        Assert.Equal(ClaudeCodeOutcome.NotStarted, result.Outcome);
        Assert.Contains("cannot find the file", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cmd_shim_runs_through_cmd_and_an_exe_directly()
    {
        var setup = Setup(SetupPlatform.Windows, new() { ["ComSpec"] = @"C:\Windows\system32\cmd.exe", ["PATH"] = @"C:\Windows" });
        string[] args = ["mcp", "add", "--scope", "user", "highlightcut", "--", @"C:\Program Files (x86)\Hi&Cut\HighlightCut.exe", "mcp"];

        var shim = setup.HiddenStart(Claude, args);
        Assert.Equal(@"C:\Windows\system32\cmd.exe", shim.FileName);
        Assert.Empty(shim.ArgumentList);
        Assert.Equal(
            $"/d /s /c \"\"{Claude}\" mcp add --scope user highlightcut -- \"C:\\Program Files (x86)\\Hi&Cut\\HighlightCut.exe\" mcp\"",
            shim.Arguments);
        Assert.True(shim.CreateNoWindow);

        var exe = setup.HiddenStart(@"C:\Users\a\.local\bin\claude.exe", args);
        Assert.Equal(@"C:\Users\a\.local\bin\claude.exe", exe.FileName);
        Assert.Equal(args, exe.ArgumentList);
    }

    // ---- The terminal scripts -----------------------------------------------------------------

    [Fact]
    public void The_windows_script_runs_the_commands_and_says_how_it_went()
    {
        string script = ClaudeSetup.WindowsScript(Claude, ClaudeSetup.Commands(@"C:\Apps\100% Cut\HighlightCut.exe", ["mcp"], removeOurCut: true));
        string[] lines = script.Split("\r\n");

        Assert.Equal("@echo off", lines[0]);
        Assert.Equal("chcp 65001 >nul", lines[1]);
        Assert.Contains("echo Adding HighlightCut to Claude Code.", lines);
        // A .cmd shim is called, so the script goes on after it; the removes may fail quietly.
        Assert.Contains($"call \"{Claude}\" mcp remove --scope user ourcut >nul 2>&1", lines);
        Assert.Contains($"call \"{Claude}\" mcp remove --scope user highlightcut >nul 2>&1", lines);
        // % is doubled in a batch file.
        Assert.Contains($"call \"{Claude}\" mcp add --scope user highlightcut -- \"C:\\Apps\\100%% Cut\\HighlightCut.exe\" mcp", lines);
        Assert.Contains("if errorlevel 1 (", lines);
        Assert.Contains(lines, l => l.Contains("echo FAILED:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("echo DONE:", StringComparison.Ordinal));
        Assert.True(script.IndexOf("mcp add", StringComparison.Ordinal) < script.IndexOf("if errorlevel", StringComparison.Ordinal));

        // An exe is run as it is.
        string exeScript = ClaudeSetup.WindowsScript(@"C:\Users\a\.local\bin\claude.exe", ClaudeSetup.Commands(Exe, ["mcp"], false));
        Assert.Contains($@"C:\Users\a\.local\bin\claude.exe mcp add --scope user highlightcut -- ""{Exe}"" mcp", exeScript.Split("\r\n"));
        Assert.DoesNotContain("ourcut", exeScript, StringComparison.Ordinal);
    }

    [Fact]
    public void The_unix_script_quotes_for_sh_and_waits_before_closing()
    {
        string script = ClaudeSetup.UnixScript("/Users/a/.local/bin/claude", ClaudeSetup.Commands("/Applications/High Cut.app/Contents/MacOS/Anna's", ["mcp"], true));
        string[] lines = script.Split('\n');

        Assert.Equal("#!/bin/sh", lines[0]);
        Assert.Contains("/Users/a/.local/bin/claude mcp remove --scope user ourcut >/dev/null 2>&1", lines);
        Assert.Contains("if /Users/a/.local/bin/claude mcp add --scope user highlightcut -- '/Applications/High Cut.app/Contents/MacOS/Anna'\\''s' mcp; then", lines);
        Assert.Contains(lines, l => l.Contains("echo 'DONE:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("echo 'FAILED:", StringComparison.Ordinal));
        Assert.Contains("read -r _", lines);
    }

    [Fact]
    public void Windows_terminal_starts_in_the_scripts_folder_and_runs_it_by_name()
    {
        var setup = Setup(SetupPlatform.Windows);
        const string Folder = @"C:\Users\Anna (Work)\AppData\Local\Temp;x\HighlightCut";
        var starts = setup.TerminalStarts(Folder + @"\add-to-claude-code-1.cmd");

        Assert.Equal(2, starts.Count);
        Assert.Equal("wt.exe", starts[0].FileName);
        // wt reads ';' as the start of its next command unless it is escaped.
        Assert.Equal(["new-tab", "--title", "HighlightCut", "-d", @"C:\Users\Anna (Work)\AppData\Local\Temp\;x\HighlightCut", "cmd.exe", "/k", "add-to-claude-code-1.cmd"],
            starts[0].ArgumentList);
        Assert.Equal("cmd.exe", starts[1].FileName);
        Assert.Equal(["/k", "add-to-claude-code-1.cmd"], starts[1].ArgumentList);
        Assert.Equal(Folder, starts[1].WorkingDirectory);
        Assert.All(starts, s => Assert.Empty(s.Arguments));
    }

    [Fact]
    public void Mac_and_linux_terminals_run_the_script_with_sh()
    {
        var mac = Setup(SetupPlatform.MacOS).TerminalStarts("/tmp/HighlightCut/add-to-claude-code-1.command");
        Assert.Equal("open", Assert.Single(mac).FileName);
        Assert.Equal(["-a", "Terminal", "/tmp/HighlightCut/add-to-claude-code-1.command"], mac[0].ArgumentList);

        var linux = Setup(SetupPlatform.Linux).TerminalStarts("/tmp/HighlightCut/add-to-claude-code-1.sh");
        Assert.Equal(["x-terminal-emulator", "gnome-terminal", "konsole", "xterm"], linux.Select(s => s.FileName));
        Assert.Equal(["--", "sh", "/tmp/HighlightCut/add-to-claude-code-1.sh"], linux[1].ArgumentList);
    }

    [Fact]
    public void Open_terminal_writes_the_script_and_falls_back_to_cmd()
    {
        var setup = Setup(SetupPlatform.Windows);
        _runner.StartResults.Enqueue(false);
        Assert.True(setup.OpenTerminal(Claude, ClaudeSetup.Commands(Exe, ["mcp"], false)));

        Assert.Equal(["wt.exe", "cmd.exe"], _runner.Started.Select(s => s.FileName));
        string file = Assert.Single(Directory.GetFiles(Path.Combine(_dir, "scripts")));
        Assert.Matches(@"add-to-claude-code-[0-9a-f]{32}\.cmd$", file);
        Assert.Equal(Path.GetFileName(file), _runner.Started[1].ArgumentList[1]);
        byte[] bytes = File.ReadAllBytes(file);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("Анна Ko", File.ReadAllText(file), StringComparison.Ordinal);

        _runner.StartResults.Enqueue(false);
        _runner.StartResults.Enqueue(false);
        Assert.False(setup.OpenTerminal(Claude, ClaudeSetup.Commands(Exe, ["mcp"], false)));
    }
}
