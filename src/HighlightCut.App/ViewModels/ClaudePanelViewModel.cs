using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.Core.Editing;

namespace HighlightCut.App.ViewModels;

public enum ClaudeLogKind
{
    User,
    Claude,
    Action,
    Live,
}

/// <summary>
/// An entry in the Claude activity log. Actions and work in progress are shown as small cards
/// ("Removed 3 silences", detail line, "18 min ago", Undo); messages are kept but not shown.
/// </summary>
public sealed partial class ClaudeLogItemViewModel : ViewModelBase
{
    private readonly Action<bool>? _setUndone;

    public ClaudeLogItemViewModel(ClaudeLogKind kind, string text, string meta = "",
        IReadOnlyList<int>? clipIds = null, double delta = 0, bool changed = false, Action<bool>? setUndone = null,
        DateTimeOffset? at = null)
    {
        Kind = kind;
        Text = text;
        Meta = meta;
        ClipIds = clipIds ?? [];
        Delta = delta;
        IsChange = changed;
        _setUndone = setUndone;
        At = at ?? DateTimeOffset.Now;
        Now = At;
    }

    public ClaudeLogKind Kind { get; }
    public string Text { get; }
    public string Meta { get; }
    public IReadOnlyList<int> ClipIds { get; }

    /// <summary>When the action happened.</summary>
    public DateTimeOffset At { get; }

    /// <summary>Change in output duration caused by this action, in seconds.</summary>
    public double Delta { get; }

    /// <summary>Counts as a highlighted change (Claude's edit) while not undone.</summary>
    public bool IsChange { get; }

    public bool IsUser => Kind == ClaudeLogKind.User;
    public bool IsClaude => Kind == ClaudeLogKind.Claude;
    public bool IsAction => Kind == ClaudeLogKind.Action;
    public bool IsLive => Kind == ClaudeLogKind.Live;

    /// <summary>Shown as a card: an action or work in progress.</summary>
    public bool IsCard => IsAction || IsLive;

    public bool CanUndo => IsAction && _setUndone is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UndoLabel), nameof(IsHighlighted), nameof(ShowUndo), nameof(ShowUndone))]
    public partial bool IsUndone { get; set; }

    public string UndoLabel => IsUndone ? "Redo" : "Undo";
    public bool IsHighlighted => IsChange && !IsUndone;

    /// <summary>The card's Undo button.</summary>
    public bool ShowUndo => CanUndo && !IsUndone;

    /// <summary>The card's "Undone" label.</summary>
    public bool ShowUndone => IsAction && IsUndone;

    /// <summary>The clock used for <see cref="WhenText"/>; the panel moves it forward.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhenText))]
    public partial DateTimeOffset Now { get; set; }

    /// <summary>"now" for work in progress, otherwise "just now", "42s ago" or "18 min ago".</summary>
    public string WhenText => IsLive ? "now" : Ago(At, Now);

    /// <summary>"just now", "42s ago", "18 min ago", or the time of day after an hour.</summary>
    internal static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        double s = Math.Max(0, Math.Round((now - at).TotalSeconds));
        return s < 5 ? "just now"
            : s < 60 ? $"{s:0}s ago"
            : s < 3600 ? $"{Math.Round(s / 60):0} min ago"
            : at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
    }

    public event EventHandler? UndoToggled;

    [RelayCommand]
    public void ToggleUndo()
    {
        if (_setUndone is null)
            return;
        IsUndone = !IsUndone;
        try
        {
            _setUndone(IsUndone);
        }
        catch (EditException)
        {
            // Refused (a later edit changed the same clips): the card stays as it was.
            IsUndone = !IsUndone;
            return;
        }
        UndoToggled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Undo on the card: reverts just this action.</summary>
    [RelayCommand]
    public void Undo()
    {
        if (ShowUndo)
            ToggleUndo();
    }
}

/// <summary>
/// The collapsible Claude panel: the project's edit history, with Claude's edits (made through the
/// MCP server) highlighted, each with its own undo. Claude is talked to through its own MCP client,
/// so the panel has no message box.
/// </summary>
public sealed partial class ClaudePanelViewModel : ViewModelBase
{
    /// <summary>How long after its last tool call Claude counts as editing.</summary>
    public static TimeSpan ActiveFor { get; set; } = TimeSpan.FromSeconds(3);

    private DispatcherTimer? _activeTimer;

    public ObservableCollection<ClaudeLogItemViewModel> Log { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    public partial bool IsOpen { get; set; } = true;

    /// <summary>Claude (its MCP client, through <c>HighlightCut mcp</c>) is connected to the editor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsComposerEnabled), nameof(McpText), nameof(Status))]
    public partial bool IsConnected { get; set; }

    /// <summary>The editor's MCP server is waiting for connections (every run but the demo).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpText), nameof(Status))]
    public partial bool IsListening { get; set; }

    /// <summary>Another HighlightCut window has the MCP server; this one gets it when that one closes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpText), nameof(Status))]
    public partial bool IsServedElsewhere { get; set; }

    /// <summary>Claude used a tool in the last <see cref="ActiveFor"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpText), nameof(StatusLine), nameof(IsWorking), nameof(Status), nameof(IsStatusLive))]
    public partial bool IsActive { get; set; }

    /// <summary>A media file is open (Claude needs one to edit).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComposerEnabled), nameof(ShowIntro), nameof(StatusLine))]
    public partial bool HasMedia { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial string Draft { get; set; } = "";

    /// <summary>Work in progress is shown in the log (the demo's scripted cards).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine), nameof(McpText), nameof(IsWorking), nameof(Status), nameof(IsStatusLive))]
    public partial bool IsBusy { get; set; }

    /// <summary>Claude is editing right now. Its own export's polling shows as Exporting, and a request as waiting, not editing.</summary>
    public bool IsWorking => IsBusy || (IsActive && !Export.IsLive && !Files.IsAsking);

    /// <summary>The header's pulsing dot and white status.</summary>
    public bool IsStatusLive => IsWorking || Export.IsLive || Files.IsAsking;

    /// <summary>Claude's export: the request banner and the card on top of the log.</summary>
    public ClaudeExportViewModel Export { get; }

    /// <summary>Claude asking to open a file or save the project.</summary>
    public ClaudeFileRequestViewModel Files { get; }

    /// <summary>What the request banner shows: an open or save request while one waits, else the export's.</summary>
    public IClaudeRequest Request => Files.IsAsking ? Files : Export;

    /// <summary>A request waits for the user's answer; Claude asks one thing at a time.</summary>
    public bool IsAsking => Export.IsAsking || Files.IsAsking;

    [ObservableProperty]
    public partial int ChangeCount { get; set; }

    public string StatusText => IsConnected ? "Connected" : "Disconnected";
    public bool IsComposerEnabled => IsConnected && HasMedia;
    public bool CanSend => IsComposerEnabled && !string.IsNullOrWhiteSpace(Draft);
    public bool ShowIntro => !HasMedia && Log.Count == 0;

    /// <summary>Nothing to show in the log yet.</summary>
    public bool HasNoCards => !Log.Any(a => a.IsCard) && !Export.HasCard;

    /// <summary>The MCP badge in the title bar.</summary>
    public string McpText => Status switch
    {
        McpStatus.Waiting => "MCP · Waiting for Claude",
        McpStatus.Connected => "MCP · Claude connected",
        McpStatus.Editing => "MCP · Claude editing",
        McpStatus.OtherWindow => "MCP · In another window",
        _ => "MCP · Off",
    };

    /// <summary>The badge's green style: Claude connected or editing.</summary>
    public bool IsMcpOn => Status is McpStatus.Connected or McpStatus.Editing;

    /// <summary>Right side of the panel header: "Editing timeline", "Exporting", "Waiting for you", "Idle · 4 actions", "Waiting for a video".</summary>
    public string StatusLine => IsWorking ? "Editing timeline"
        : Export.Stage == ClaudeExportStage.Running ? "Exporting"
        : Export.Stage == ClaudeExportStage.Requested || Files.IsAsking ? "Waiting for you"
        : !HasMedia ? "Waiting for a video"
        : IsOpen ? "Idle"
        : $"Idle · {Log.Count(a => a.IsAction && !a.IsUndone)} actions";

    public event EventHandler? Changed;

    public ClaudePanelViewModel(ClaudeExportViewModel export, ClaudeFileRequestViewModel files)
    {
        Export = export;
        Files = files;
        Log.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowIntro));
            OnPropertyChanged(nameof(HasNoCards));
            Recount();
        };
        export.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ClaudeExportViewModel.Stage))
                return;
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(IsWorking));
            OnPropertyChanged(nameof(IsStatusLive));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(HasNoCards));
        };
        files.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ClaudeFileRequestViewModel.IsAsking))
                return;
            OnPropertyChanged(nameof(Request));
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(IsWorking));
            OnPropertyChanged(nameof(IsStatusLive));
            OnPropertyChanged(nameof(Status));
        };
        // Whatever moves the status moves the badge.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Status))
                return;
            OnPropertyChanged(nameof(McpText));
            OnPropertyChanged(nameof(IsMcpOn));
        };
    }

    public void Add(ClaudeLogItemViewModel item)
    {
        item.UndoToggled += (_, _) => Recount();
        Log.Add(item);
    }

    public void Recount()
    {
        ChangeCount = Log.Count(a => a.IsHighlighted);
        IsBusy = Log.Any(a => a.IsLive);
        OnPropertyChanged(nameof(StatusLine));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Claude just used a tool: shows it as editing for a moment.</summary>
    public void NoteActivity()
    {
        IsActive = true;
        _activeTimer ??= new DispatcherTimer(ActiveFor, DispatcherPriority.Background, (_, _) =>
        {
            IsActive = false;
            _activeTimer?.Stop();
        });
        _activeTimer.Stop();
        _activeTimer.Start();
    }

    /// <summary>Moves the clock of every card to <paramref name="now"/> ("18 min ago").</summary>
    public void RefreshTimes(DateTimeOffset now)
    {
        foreach (var item in Log)
            item.Now = now;
        Export.Now = now;
    }

    [RelayCommand]
    private void ToggleOpen() => IsOpen = !IsOpen;

    [RelayCommand]
    private void ToggleConnection() => IsConnected = !IsConnected;

    [RelayCommand]
    private void UndoAll()
    {
        foreach (var a in Log.Where(a => a.IsHighlighted).ToList())
            a.ToggleUndo();
    }

    /// <summary>Stops the work in progress; nothing it has not applied yet is changed.</summary>
    [RelayCommand]
    private void Stop()
    {
        foreach (var live in Log.Where(a => a.IsLive).ToList())
            Log.Remove(live);
    }

    [RelayCommand]
    private void Send()
    {
        if (!CanSend)
            return;
        Add(new ClaudeLogItemViewModel(ClaudeLogKind.User, Draft.Trim()));
        Draft = "";
    }
}
