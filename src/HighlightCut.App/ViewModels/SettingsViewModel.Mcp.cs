using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.App.Demo;
using HighlightCut.App.Services;

namespace HighlightCut.App.ViewModels;

/// <summary>Settings → MCP server: the server switch and its status, permissions, and how to add HighlightCut to Claude.</summary>
public sealed partial class SettingsViewModel
{
    public static IReadOnlyList<McpPermission> AllowOrAsk { get; } = [McpPermission.Allow, McpPermission.Ask];
    public static IReadOnlyList<McpPermission> AllPermissions { get; } = [McpPermission.Allow, McpPermission.Ask, McpPermission.Never];

    private DispatcherTimer? _mcpClock;
    private DispatcherTimer? _claudeCodeCopied;
    private DispatcherTimer? _claudeDesktopCopied;
    private bool _designMcp;

    /// <summary>"Let Claude connect": the MCP server runs.</summary>
    [ObservableProperty]
    public partial bool McpEnabled { get; set; } = true;

    [ObservableProperty]
    public partial McpPermission OpenFilesPermission { get; set; } = McpPermission.Allow;

    [ObservableProperty]
    public partial McpPermission SaveProjectPermission { get; set; } = McpPermission.Ask;

    /// <summary>
    /// For Claude's exports: Allow starts them right away, Ask shows a request first, Never leaves exporting to the user.
    /// "Always allow" on the request sets Allow.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportPermissionNote))]
    public partial McpPermission ExportPermission { get; set; } = McpPermission.Ask;

    private ChoiceSet<McpPermission>? _openFilesChoices;
    public IReadOnlyList<ChoiceOption> OpenFilesOptions =>
        (_openFilesChoices ??= new(Labelled(AllowOrAsk), p => OpenFilesPermission = p, OpenFilesPermission)).Options;

    private ChoiceSet<McpPermission>? _saveProjectChoices;
    public IReadOnlyList<ChoiceOption> SaveProjectOptions =>
        (_saveProjectChoices ??= new(Labelled(AllowOrAsk), p => SaveProjectPermission = p, SaveProjectPermission)).Options;

    private ChoiceSet<McpPermission>? _exportChoices;
    public IReadOnlyList<ChoiceOption> ExportOptions =>
        (_exportChoices ??= new(Labelled(AllPermissions), p => ExportPermission = p, ExportPermission)).Options;

    public string ExportPermissionNote => ExportPermission switch
    {
        McpPermission.Allow => "Exports start right away. You can cancel them in the Claude panel.",
        McpPermission.Never => "Claude can prepare the timeline. Only you can export.",
        _ => "HighlightCut shows a request with the clips, file and format. Nothing is written until you allow it.",
    };

    // ---- Status card ------------------------------------------------------------------------

    public McpStatus ConnectionStatus => Claude.Status;

    public string McpStatusTitle => ConnectionStatus switch
    {
        McpStatus.Waiting => "Waiting for Claude",
        McpStatus.Connected => "Claude connected",
        McpStatus.Editing => "Claude editing",
        McpStatus.OtherWindow => "Running in another window",
        _ => "Not running",
    };

    public string McpStatusDetail => ConnectionStatus switch
    {
        McpStatus.Waiting => "The server is running. Add HighlightCut to Claude Code or Claude Desktop below, then start a chat.",
        McpStatus.Connected => $"{Claude.ClientName ?? "Claude"} · {ConnectedText()}",
        McpStatus.Editing => $"{Claude.ClientName ?? "Claude"} · {ConnectedText()} · editing the timeline",
        McpStatus.OtherWindow => Claude.OtherWindowProject is { } project
            ? $"The HighlightCut window with {project} has the server. Close that window to connect Claude here."
            : "Another HighlightCut window has the server. Close that window to connect Claude here.",
        _ => "Claude can’t reach HighlightCut while this is off.",
    };

    public bool IsMcpDotEditing => ConnectionStatus == McpStatus.Editing;
    public bool IsMcpDotConnected => ConnectionStatus == McpStatus.Connected;
    public bool IsMcpDotOff => ConnectionStatus == McpStatus.Off;
    public bool IsMcpDotIdle => ConnectionStatus is McpStatus.Waiting or McpStatus.OtherWindow;

    /// <summary>The time the "connected 12 min" is measured to; a seam for tests.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    private string ConnectedText()
    {
        if (Claude.ConnectedSince is not { } since)
            return "connected";
        int minutes = (int)Math.Floor((Clock() - since).TotalMinutes);
        if (minutes < 1)
            return "connected just now";
        if (minutes < 60)
            return $"connected {minutes.ToString(CultureInfo.InvariantCulture)} min";
        string text = $"connected {(minutes / 60).ToString(CultureInfo.InvariantCulture)} h";
        return minutes % 60 > 0 ? text + $" {(minutes % 60).ToString(CultureInfo.InvariantCulture)} min" : text;
    }

    private void RaiseMcpStatus()
    {
        OnPropertyChanged(nameof(ConnectionStatus));
        OnPropertyChanged(nameof(McpStatusTitle));
        OnPropertyChanged(nameof(McpStatusDetail));
        OnPropertyChanged(nameof(IsMcpDotEditing));
        OnPropertyChanged(nameof(IsMcpDotConnected));
        OnPropertyChanged(nameof(IsMcpDotOff));
        OnPropertyChanged(nameof(IsMcpDotIdle));
    }

    // Keeps "connected 12 min" current while the section is on screen.
    private void UpdateMcpClock()
    {
        if (IsOpen && IsMcp)
        {
            if (_mcpClock is null)
            {
                _mcpClock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
                _mcpClock.Tick += (_, _) => OnPropertyChanged(nameof(McpStatusDetail));
            }
            _mcpClock.Start();
        }
        else
        {
            _mcpClock?.Stop();
        }
    }

    // ---- Connect Claude ---------------------------------------------------------------------

    /// <summary>The program Claude starts (this HighlightCut) and its arguments.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClaudeCodeCommand), nameof(ClaudeDesktopConfig))]
    public partial string McpCommand { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClaudeCodeCommand), nameof(ClaudeDesktopConfig))]
    public partial IReadOnlyList<string> McpArgs { get; private set; } = [];

    /// <summary>
    /// The app was called OurCut before (its folder was moved on the first start after the rename): Claude Code and
    /// Claude Desktop still have it under the old name and program, so the section says to add it again.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowRenameNote { get; set; }

    /// <summary>Adds HighlightCut to Claude Code, for every project.</summary>
    public string ClaudeCodeCommand =>
        "claude mcp add --scope user highlightcut -- " + string.Join(' ', McpArgs.Prepend(McpCommand).Select(a => Quote(a, _designMcp || OperatingSystem.IsWindows())));

    /// <summary>The entry for Claude Desktop's claude_desktop_config.json, with <c>args</c> on one line as in the design.</summary>
    public string ClaudeDesktopConfig
    {
        get
        {
            string command = JsonSerializer.Serialize(McpCommand, IndentedJson);
            string args = string.Join(", ", McpArgs.Select(a => JsonSerializer.Serialize(a, IndentedJson)));
            return "{\n  \"mcpServers\": {\n    \"highlightcut\": {\n      \"command\": " + command + ",\n      \"args\": [" + args + "]\n    }\n  }\n}";
        }
    }

    /// <summary>Where Claude Desktop keeps its settings on this system.</summary>
    public static string ClaudeDesktopConfigPath =>
        OperatingSystem.IsWindows() ? @"%APPDATA%\Claude\claude_desktop_config.json"
        : OperatingSystem.IsMacOS() ? "~/Library/Application Support/Claude/claude_desktop_config.json"
        : "~/.config/Claude/claude_desktop_config.json";

    /// <summary>The path shown after "Add to" (the design's Windows path in demo mode).</summary>
    public string McpConfigPathText => _designMcp ? DesignSettingsSample.ConfigPath : ClaudeDesktopConfigPath;

    /// <summary><see cref="ClaudeDesktopConfigPath"/> with the folders filled in.</summary>
    public static string ClaudeDesktopConfigFile
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS())
                return Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json");
            string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".config");
            return Path.Combine(config, "Claude", "claude_desktop_config.json");
        }
    }

    // Relaxed escaping keeps non-ASCII paths readable (no \uXXXX); the text is only pasted into a JSON file.
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // The demo shows the design's Windows path, so it quotes the Windows way on every OS.
    private static string Quote(string arg, bool windows) =>
        arg.Length > 0 && !arg.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '\'' or '&' or '(' or ')' or ';') ? arg
        : windows ? '"' + arg + '"'
        : "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClaudeCodeCopyLabel))]
    public partial bool ClaudeCodeCopied { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClaudeDesktopCopyLabel))]
    public partial bool ClaudeDesktopCopied { get; private set; }

    public string ClaudeCodeCopyLabel => ClaudeCodeCopied ? "Copied" : "Copy";
    public string ClaudeDesktopCopyLabel => ClaudeDesktopCopied ? "Copied" : "Copy";

    [RelayCommand]
    private async Task CopyClaudeCode()
    {
        await (CopyText?.Invoke(ClaudeCodeCommand) ?? Task.CompletedTask).ConfigureAwait(true);
        ClaudeCodeCopied = true;
        Restart(_claudeCodeCopied ??= CopiedTimer(() => ClaudeCodeCopied = false));
    }

    [RelayCommand]
    private async Task CopyClaudeDesktop()
    {
        await (CopyText?.Invoke(ClaudeDesktopConfig) ?? Task.CompletedTask).ConfigureAwait(true);
        ClaudeDesktopCopied = true;
        Restart(_claudeDesktopCopied ??= CopiedTimer(() => ClaudeDesktopCopied = false));
    }

    // "Copied" goes back to "Copy" 1.5 s after the last click.
    private static DispatcherTimer CopiedTimer(Action revert)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            revert();
        };
        return timer;
    }

    private static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    // ---- Add to Claude ------------------------------------------------------------------------

    /// <summary>Finds and runs the claude CLI and opens terminals; tests replace it so nothing is started.</summary>
    internal ClaudeSetup Setup { get; set; } = ClaudeSetup.ForThisSystem();

    /// <summary>The claude_desktop_config.json files to add HighlightCut to; tests replace it.</summary>
    internal Func<IReadOnlyList<string>> ClaudeDesktopConfigFiles { get; set; } = () => DesktopConfigFile.Files(ClaudeDesktopConfigFile,
        OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : null);

    /// <summary>Opens a file or web page in its app; tests replace it.</summary>
    internal Action<string> OpenFile { get; set; } = FileManager.Open;

    /// <summary>What Add to Claude Code or Open terminal did; null before.</summary>
    [ObservableProperty]
    public partial string? ClaudeCodeResult { get; private set; }

    /// <summary>The claude CLI was not found: the result says so, with a link to install it.</summary>
    [ObservableProperty]
    public partial bool IsClaudeCodeMissing { get; private set; }

    /// <summary>What Add to Claude Desktop or Open file did; null before.</summary>
    [ObservableProperty]
    public partial string? ClaudeDesktopResult { get; private set; }

    public const string AddedToClaudeCode = "Added to Claude Code. Start a new Claude Code chat.";

    /// <summary>After the file is changed: Claude Desktop reads it only when it starts, and closing its window doesn’t quit it.</summary>
    public static string RestartClaudeDesktop =>
        OperatingSystem.IsWindows() ? "Quit Claude Desktop fully (right-click its tray icon → Quit) and start it again."
        : OperatingSystem.IsMacOS() ? "Quit Claude Desktop fully (⌘Q) and start it again."
        : "Quit Claude Desktop fully and start it again.";

    /// <summary>Forgets what the buttons did (the welcome tour opens clean).</summary>
    public void ClearClaudeResults()
    {
        ClaudeCodeResult = null;
        ClaudeDesktopResult = null;
        IsClaudeCodeMissing = false;
    }

    // After the rename the old "ourcut" server is removed too.
    private IReadOnlyList<ClaudeCommand> ClaudeCodeSteps() => ClaudeSetup.Commands(McpCommand, McpArgs, removeOurCut: ShowRenameNote);

    private string? FindClaude()
    {
        IsClaudeCodeMissing = false;
        if (Setup.FindClaude() is { } claude)
            return claude;
        IsClaudeCodeMissing = true;
        ClaudeCodeResult = "Claude Code isn’t installed, or it’s somewhere HighlightCut doesn’t look. Install it and try again, or copy the command.";
        return null;
    }

    /// <summary>Runs claude mcp remove and add hidden, and shows how it went.</summary>
    [RelayCommand]
    private async Task AddToClaudeCode()
    {
        if (_editor.IsDemo)
        {
            ClaudeCodeResult = AddedToClaudeCode;
            return;
        }
        if (FindClaude() is not { } claude)
            return;
        ClaudeCodeResult = "Adding to Claude Code…";
        var result = await Setup.AddToClaudeCodeAsync(claude, ClaudeCodeSteps()).ConfigureAwait(true);
        ClaudeCodeResult = result.Outcome switch
        {
            ClaudeCodeOutcome.Added => AddedToClaudeCode,
            ClaudeCodeOutcome.TimedOut => "Claude Code didn’t answer in 30 seconds. Try Open terminal to see what it’s doing.",
            ClaudeCodeOutcome.NotStarted => $"Couldn’t start Claude Code: {result.Message.TrimEnd('.')}. Try Open terminal.",
            _ when result.Message.Length > 0 => $"Claude Code couldn’t add it: {result.Message}",
            _ => "Claude Code couldn’t add it. Try Open terminal to see why.",
        };
    }

    /// <summary>The same steps in a terminal window, to watch them or answer Claude Code.</summary>
    [RelayCommand]
    private async Task OpenClaudeCodeTerminal()
    {
        if (_editor.IsDemo)
        {
            ClaudeCodeResult = "Running in a new terminal. Start a new Claude Code chat when it says DONE.";
            return;
        }
        if (FindClaude() is not { } claude)
            return;
        if (Setup.OpenTerminal(claude, ClaudeCodeSteps()))
        {
            ClaudeCodeResult = "Running in a new terminal. Start a new Claude Code chat when it says DONE.";
        }
        else
        {
            await CopyClaudeCodeCommand.ExecuteAsync(null).ConfigureAwait(true);
            ClaudeCodeResult = "Couldn’t open a terminal. The command is copied: paste it into one.";
        }
    }

    [RelayCommand]
    private void InstallClaudeCode()
    {
        if (!_editor.IsDemo)
            OpenFile(ClaudeSetup.InstallPage);
    }

    /// <summary>
    /// Puts HighlightCut's entry into Claude Desktop's settings file (the Store app's too), keeping the rest. If a file
    /// can’t be changed it stays as it was: it opens, and the entry is copied to add by hand.
    /// </summary>
    [RelayCommand]
    private async Task AddToClaudeDesktop()
    {
        if (_editor.IsDemo)
        {
            ClaudeDesktopResult = "Added to Claude Desktop. " + RestartClaudeDesktop;
            return;
        }
        var results = ClaudeDesktopConfigFiles().Select(f => DesktopConfigFile.Add(f, McpCommand, McpArgs)).ToList();
        if (results.FirstOrDefault(r => r.Outcome == DesktopConfigOutcome.Failed) is { } failed)
        {
            await CopyClaudeDesktopCommand.ExecuteAsync(null).ConfigureAwait(true);
            bool open = File.Exists(failed.File);
            if (open)
                OpenFile(failed.File);
            ClaudeDesktopResult = $"Nothing was changed: {DesktopConfigFile.FileName} {failed.Error}. "
                + (open ? "It’s open and the entry is copied: add it under mcpServers." : "The entry is copied: add it yourself.");
            return;
        }
        string text = results.All(r => r.Outcome == DesktopConfigOutcome.AlreadyAdded) ? "HighlightCut is already in Claude Desktop."
            : results.Any(r => r.RemovedOurCut) ? "Added to Claude Desktop, in place of the old OurCut entry."
            : "Added to Claude Desktop.";
        ClaudeDesktopResult = text + " " + RestartClaudeDesktop;
    }

    [RelayCommand]
    private void OpenClaudeDesktopConfig()
    {
        if (_editor.IsDemo)
            return;
        if (ClaudeDesktopConfigFiles().FirstOrDefault(File.Exists) is { } file)
            OpenFile(file);
        else
            ClaudeDesktopResult = $"There’s no {DesktopConfigFile.FileName} yet. Add to Claude Desktop makes it.";
    }

    // ---- Init, load, save --------------------------------------------------------------------

    private void InitMcp()
    {
        Claude.PropertyChanged += OnClaudeChanged;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsOpen) or nameof(Section))
            {
                if (IsOpen)
                {
                    LeaveDesignMcp();
                    RaiseMcpStatus();
                }
                UpdateMcpClock();
            }
        };
    }

    private void OnClaudeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClaudePanelViewModel.Status) or nameof(ClaudePanelViewModel.ConnectedSince)
            or nameof(ClaudePanelViewModel.ClientName) or nameof(ClaudePanelViewModel.OtherWindowProject))
            RaiseMcpStatus();
    }

    private static (McpPermission, string)[] Labelled(IEnumerable<McpPermission> permissions) =>
        [.. permissions.Select(p => (p, p.ToString()))];

    private void SyncMcpChoices()
    {
        _openFilesChoices?.Select(OpenFilesPermission);
        _saveProjectChoices?.Select(SaveProjectPermission);
        _exportChoices?.Select(ExportPermission);
    }

    private void LoadMcp(McpSettings? saved)
    {
        var s = saved ?? new McpSettings();
        McpEnabled = s.Enabled;
        OpenFilesPermission = Parse(s.OpenFiles, AllowOrAsk, McpPermission.Allow);
        SaveProjectPermission = Parse(s.SaveProject, AllowOrAsk, McpPermission.Ask);
        ExportPermission = Parse(s.Export, AllPermissions, McpPermission.Ask);
        SyncMcpChoices();
        // A value this version does not know is saved back as the one shown.
        if (saved is not null)
            UpdateSettings(a => a with { Mcp = ToMcpSettings() });
    }

    private static McpPermission Parse(string? value, IReadOnlyList<McpPermission> allowed, McpPermission fallback) =>
        Enum.TryParse<McpPermission>(value, out var p) && allowed.Contains(p) ? p : fallback;

    private McpSettings ToMcpSettings() =>
        new(McpEnabled, OpenFilesPermission.ToString(), SaveProjectPermission.ToString(), ExportPermission.ToString());

    private void SaveMcp()
    {
        SyncMcpChoices();
        if (!_loading)
            UpdateSettings(s => s with { Mcp = ToMcpSettings() });
    }

    partial void OnMcpEnabledChanged(bool value)
    {
        Claude.IsServerOn = value;
        SaveMcp();
    }

    partial void OnOpenFilesPermissionChanged(McpPermission value) => SaveMcp();

    partial void OnSaveProjectPermissionChanged(McpPermission value) => SaveMcp();

    partial void OnExportPermissionChanged(McpPermission value) => SaveMcp();

    /// <summary>The design's MCP sample (demo mode): Windows paths and the default permissions.</summary>
    internal void LoadDesignMcp()
    {
        bool wasLoading = _loading;
        _loading = true;
        _designMcp = true;
        McpEnabled = true;
        OpenFilesPermission = McpPermission.Allow;
        SaveProjectPermission = McpPermission.Ask;
        ExportPermission = McpPermission.Ask;
        McpCommand = DesignSettingsSample.McpCommand;
        McpArgs = ["mcp"];
        _loading = wasLoading;
        OnPropertyChanged(nameof(McpConfigPathText));
        RaiseMcpStatus();
    }

    // After the demo, a real file was opened: show this HighlightCut again.
    private void LeaveDesignMcp()
    {
        if (!_designMcp || _editor.IsDemo)
            return;
        _designMcp = false;
        (McpCommand, McpArgs) = EditorLauncher.McpCommand();
        OnPropertyChanged(nameof(McpConfigPathText));
    }
}
