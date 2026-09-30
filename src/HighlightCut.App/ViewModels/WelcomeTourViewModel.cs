using System.ComponentModel;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.App.Services;

namespace HighlightCut.App.ViewModels;

/// <summary>A step in the welcome tour's list: done steps get a tick, the current one a bone circle.</summary>
public sealed partial class WelcomeStepViewModel(WelcomeTourViewModel tour, int index, string title) : ViewModelBase
{
    public int Index { get; } = index;
    public string Title { get; } = title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Number), nameof(AccessibleName))]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Number), nameof(AccessibleName))]
    public partial bool IsDone { get; set; }

    public string Number => IsDone ? "✓" : (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>What a screen reader says for the row: "Step 2 of 4, Shortcuts, current".</summary>
    public string AccessibleName => $"Step {Index + 1} of {tour.Steps.Count}, {Title}" + (IsCurrent ? ", current" : IsDone ? ", done" : "");

    [RelayCommand]
    private void Go() => tour.Step = Index;
}

/// <summary>A row of the Shortcuts step: what it does and its key in Settings → Keyboard.</summary>
public sealed record WelcomeKey(string Action, string Key);

/// <summary>
/// The first-launch welcome tour (design/project/HighlightCut Onboarding.dc.html, 1b): a dialog over the empty editor
/// with four steps, which opens by itself once and again from the project menu → Welcome tour.
/// </summary>
public sealed partial class WelcomeTourViewModel : ViewModelBase
{
    public static IReadOnlyList<string> Titles { get; } = ["Open & cut", "Shortcuts", "Transcript", "Connect Claude"];

    private readonly EditorViewModel _editor;
    private readonly ChoiceSet<bool> _clientChoices;
    private DispatcherTimer? _noteTimer;

    public WelcomeTourViewModel(EditorViewModel editor)
    {
        _editor = editor;
        Steps = [.. Titles.Select((t, i) => new WelcomeStepViewModel(this, i, t))];
        _clientChoices = new([(true, "Claude Code"), (false, "Claude Desktop")], code => IsClaudeCode = code, IsClaudeCode);
        SyncSteps();
        Settings.KeyMap.Changed += (_, _) => OnPropertyChanged(nameof(Keys));
        Settings.PropertyChanged += OnSettingsChanged;
        // Opening a video (dropped on the window, or from Claude) ends the tour.
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.HasFile) && editor.HasFile && IsOpen)
                Close();
        };
    }

    public SettingsViewModel Settings => _editor.Settings;

    public IReadOnlyList<WelcomeStepViewModel> Steps { get; }

    /// <summary>The dialog is shown.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>After the tour closes: "Reopen the tour anytime…" with Replay, for a few seconds.</summary>
    [ObservableProperty]
    public partial bool IsNoteVisible { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenAndCut), nameof(IsShortcuts), nameof(IsTranscript), nameof(IsConnectClaude), nameof(IsLast),
        nameof(HasBack), nameof(StepText), nameof(StepAnnouncement), nameof(NextLabel))]
    public partial int Step { get; set; }

    public bool IsOpenAndCut => Step == 0;
    public bool IsShortcuts => Step == 1;
    public bool IsTranscript => Step == 2;
    public bool IsConnectClaude => Step == 3;
    public bool IsLast => Step == Steps.Count - 1;
    public bool HasBack => Step > 0;
    public string StepText => $"{Step + 1} of {Steps.Count}";

    /// <summary>Read out when the step changes: "Step 2 of 4: Shortcuts".</summary>
    public string StepAnnouncement => $"Step {Step + 1} of {Steps.Count}: {Steps[Step].Title}";

    public string NextLabel => IsLast ? "Open a video" : "Continue";

    partial void OnStepChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, Steps.Count - 1);
        if (clamped != value)
        {
            Step = clamped;
            return;
        }
        SyncSteps();
    }

    private void SyncSteps()
    {
        foreach (var s in Steps)
        {
            s.IsCurrent = s.Index == Step;
            s.IsDone = s.Index < Step;
        }
    }

    /// <summary>At start: the first launch of an editor that stays empty (no file to open, no last project to reopen).</summary>
    public bool ShouldOpenAtStart(string? file) =>
        !_editor.IsDemo && file is null && !Settings.WelcomeTourSeen
        && !(Settings.Startup == StartupAction.OpenLastProject && _editor.RecentFiles.Count > 0);

    /// <summary>Shows the tour from its first step (project menu → Welcome tour, Replay).</summary>
    [RelayCommand]
    public void Open()
    {
        StopNote();
        Step = 0;
        Settings.ClearClaudeResults();
        IsOpen = true;
    }

    /// <summary>Skip tour, Esc: closes it, says where to reopen it, and does not open it by itself again.</summary>
    [RelayCommand]
    public void Close()
    {
        if (!IsOpen)
            return;
        IsOpen = false;
        Settings.MarkWelcomeTourSeen();
        IsNoteVisible = true;
        _noteTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(8), DispatcherPriority.Background, (_, _) => StopNote());
        _noteTimer.Stop();
        _noteTimer.Start();
    }

    /// <summary>Hides the tour and its note without saving anything (demo screens start from here).</summary>
    public void Reset()
    {
        StopNote();
        IsOpen = false;
        Step = 0;
        IsClaudeCode = true;
        Settings.ClearClaudeResults();
    }

    private void StopNote()
    {
        _noteTimer?.Stop();
        IsNoteVisible = false;
    }

    [RelayCommand]
    private void Back() => Step = Math.Max(0, Step - 1);

    /// <summary>Continue; on the last step "Open a video" closes the tour and opens one (Ctrl+O does the same).</summary>
    [RelayCommand]
    private void Next()
    {
        if (IsLast)
            Finish();
        else
            Step++;
    }

    /// <summary>
    /// The dialog's own keys, when focus is not in a control that needs them: Enter continues (or opens a video on the
    /// last step), → and ← go to the next and previous step. Esc and the Open video key are <see cref="Shortcuts"/>'.
    /// </summary>
    public bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Enter:
                Next();
                return true;
            case Key.Right:
                if (!IsLast)
                    Step++;
                return true;
            case Key.Left:
                Back();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Closes the tour and asks for a video.</summary>
    public void Finish()
    {
        Close();
        _editor.OpenFileCommand.Execute(null);
    }

    // ---- Shortcuts ----------------------------------------------------------------------------

    /// <summary>The six shortcuts the tour shows, with the keys they have now.</summary>
    public IReadOnlyList<WelcomeKey> Keys
    {
        get
        {
            var k = Settings.Keys;
            return
            [
                new("Play / pause", k.PlayPause),
                new("Step one frame, Shift for a second",
                    $"{k.Label(ShortcutAction.PreviousFrame)} {k.Label(ShortcutAction.NextFrame)}"),
                new("Set in point", k.SetIn),
                new("Set out point", k.SetOut),
                new("Split at playhead", k.Label(ShortcutAction.Split)),
                new("Keep or exclude clip", k.Label(ShortcutAction.ToggleExclude)),
            ];
        }
    }

    // ---- Transcript ---------------------------------------------------------------------------

    /// <summary>
    /// Parakeet's download when no model is installed, the same one as the Transcript tab's. It downloads only: with no
    /// video open there is nothing to transcribe, and the download goes on when the tour is closed.
    /// </summary>
    public ModelOfferViewModel ModelOffer => _editor.TranscriptPanel.ModelOffer;

    public string TranscriptText => "Select words to make a clip, or remove filler words in one go. "
        + (_editor.IsDemo || Settings.TranscribesOnGpu ? "Transcription runs on your GPU." : "Transcription runs on this computer.")
        + " Nothing leaves your machine.";

    // ---- Connect Claude -----------------------------------------------------------------------

    public IReadOnlyList<ChoiceOption> ClientOptions => _clientChoices.Options;

    /// <summary>Claude Code (a terminal command) or Claude Desktop (its settings file).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Command), nameof(CommandHint), nameof(CopyLabel), nameof(SetupLabel), nameof(OtherSetupLabel),
        nameof(SetupNote), nameof(IsInstallLinkVisible))]
    public partial bool IsClaudeCode { get; set; } = true;

    partial void OnIsClaudeCodeChanged(bool value) => _clientChoices.Select(value);

    public string Command => IsClaudeCode ? Settings.ClaudeCodeCommand : Settings.ClaudeDesktopConfig;

    public string CommandHint => IsClaudeCode
        ? "Add to Claude Code runs this for you. You can also paste it into a terminal."
        : $"Add to Claude Desktop puts this in {Settings.McpConfigPathText} and keeps your other servers.";

    public string CopyLabel => IsClaudeCode ? Settings.ClaudeCodeCopyLabel : Settings.ClaudeDesktopCopyLabel;

    [RelayCommand]
    private Task Copy() => IsClaudeCode ? Settings.CopyClaudeCodeCommand.ExecuteAsync(null) : Settings.CopyClaudeDesktopCommand.ExecuteAsync(null);

    public string SetupLabel => IsClaudeCode ? "Add to Claude Code" : "Add to Claude Desktop";

    /// <summary>The way to do it by hand: watch it run in a terminal, or open the file.</summary>
    public string OtherSetupLabel => IsClaudeCode ? "Open terminal" : "Open config file";

    /// <summary>What the buttons did (the same as Settings → MCP server shows), or what they will do.</summary>
    public string SetupNote => IsClaudeCode
        ? Settings.ClaudeCodeResult ?? "Adds HighlightCut for all your projects. Adding it again updates the entry."
        : Settings.ClaudeDesktopResult ?? "Your other servers stay, and the old file is kept as a .bak.";

    /// <summary>Claude Code isn’t installed: Install Claude Code next to the note.</summary>
    public bool IsInstallLinkVisible => IsClaudeCode && Settings.IsClaudeCodeMissing;

    /// <summary>Add to Claude Code (claude mcp add, hidden) or Add to Claude Desktop (merges the settings file).</summary>
    [RelayCommand]
    private Task RunSetup() => IsClaudeCode ? Settings.AddToClaudeCodeCommand.ExecuteAsync(null) : Settings.AddToClaudeDesktopCommand.ExecuteAsync(null);

    /// <summary>Open terminal (the same steps, visible) or Open config file.</summary>
    [RelayCommand]
    private Task RunOtherSetup()
    {
        if (IsClaudeCode)
            return Settings.OpenClaudeCodeTerminalCommand.ExecuteAsync(null);
        Settings.OpenClaudeDesktopConfigCommand.Execute(null);
        return Task.CompletedTask;
    }

    /// <summary>The status badge: green once Claude is connected.</summary>
    public bool IsClaudeConnected => Settings.ConnectionStatus is McpStatus.Connected or McpStatus.Editing;

    public string ClaudeStatusText => IsClaudeConnected ? "Claude connected" : Settings.McpStatusTitle;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.ConnectionStatus) or nameof(SettingsViewModel.McpStatusTitle):
                OnPropertyChanged(nameof(IsClaudeConnected));
                OnPropertyChanged(nameof(ClaudeStatusText));
                break;
            case nameof(SettingsViewModel.ClaudeCodeCommand) or nameof(SettingsViewModel.ClaudeDesktopConfig):
                OnPropertyChanged(nameof(Command));
                break;
            case nameof(SettingsViewModel.McpConfigPathText):
                OnPropertyChanged(nameof(CommandHint));
                break;
            case nameof(SettingsViewModel.ClaudeCodeCopyLabel) or nameof(SettingsViewModel.ClaudeDesktopCopyLabel):
                OnPropertyChanged(nameof(CopyLabel));
                break;
            case nameof(SettingsViewModel.ClaudeCodeResult) or nameof(SettingsViewModel.ClaudeDesktopResult):
                OnPropertyChanged(nameof(SetupNote));
                break;
            case nameof(SettingsViewModel.IsClaudeCodeMissing):
                OnPropertyChanged(nameof(IsInstallLinkVisible));
                break;
            case nameof(SettingsViewModel.Device) or nameof(SettingsViewModel.Model):
                OnPropertyChanged(nameof(TranscriptText));
                break;
        }
    }
}
