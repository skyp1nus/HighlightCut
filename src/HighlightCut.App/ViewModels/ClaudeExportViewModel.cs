using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.App.Services;
using HighlightCut.Core.Time;

namespace HighlightCut.App.ViewModels;

/// <summary>The user's answer to Claude's export request.</summary>
public enum ClaudeExportAnswer
{
    Allow,
    AlwaysAllow,
    Deny,
}

/// <summary>Where Claude's export stands, as its card shows it.</summary>
public enum ClaudeExportStage
{
    None,
    Requested,
    Running,
    Done,
    Failed,
    Denied,
    Cancelled,
}

/// <summary>What Claude's export writes, as the request banner and the card name it.</summary>
/// <param name="Path">The merged file, or the folder for separate files (display text).</param>
/// <param name="Name">"keynote-cut.mp4", or "4 files".</param>
/// <param name="Folder">Where the files go.</param>
/// <param name="Mode">"Lossless" or "Re-encode".</param>
/// <param name="Container">"MP4", "MOV" or "MKV".</param>
/// <param name="ClipCount">Included clips.</param>
/// <param name="Duration">Output length in seconds.</param>
/// <param name="Merged">One file rather than one per clip.</param>
public sealed record ClaudeExportTarget(string Path, string Name, string Folder, string Mode, string Container,
    int ClipCount, double Duration, bool Merged = true)
{
    /// <summary>"4 clips · 4:47".</summary>
    public string ClipsText => $"{ClipCount} {(ClipCount == 1 ? "clip" : "clips")} · {TimeFormat.WholeSeconds(Duration)}";
}

/// <summary>
/// Claude's export as the editor shows it: the request over the preview and the card at the top of
/// the Claude panel. It follows <see cref="ExportViewModel"/> whenever an export started by Claude runs.
/// </summary>
public sealed partial class ClaudeExportViewModel : ViewModelBase, IClaudeRequest
{
    private readonly EditorViewModel _editor;
    private TaskCompletionSource<ClaudeExportAnswer>? _answer;
    private ClaudeExportTarget? _next;
    private double _stoppedAt;
    private string _sizeText = "";
    private string? _revealPath;

    public ClaudeExportViewModel(EditorViewModel editor)
    {
        _editor = editor;
        editor.Export.PropertyChanged += OnExportChanged;
        // A new or closed file drops the card and withdraws a pending request.
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.Media))
                Clear();
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCard), nameof(IsLive), nameof(IsDone), nameof(IsFailed), nameof(IsEnded), nameof(ShowProgress),
        nameof(CanCancel), nameof(CanReveal), nameof(CanRetry), nameof(Title), nameof(Detail), nameof(WhenText))]
    public partial ClaudeExportStage Stage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequestTitle), nameof(RequestDetail), nameof(Title), nameof(Detail))]
    public partial ClaudeExportTarget? Target { get; private set; }

    /// <summary>The request banner is up, waiting for Allow, Always allow or Deny.</summary>
    [ObservableProperty]
    public partial bool IsAsking { get; private set; }

    /// <summary>Why the export failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial string? Error { get; private set; }

    /// <summary>When the card's stage began.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhenText))]
    public partial DateTimeOffset At { get; private set; }

    /// <summary>The clock used for <see cref="WhenText"/>; the panel moves it forward.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhenText))]
    public partial DateTimeOffset Now { get; set; }

    public bool HasCard => Stage != ClaudeExportStage.None;
    public bool IsLive => Stage is ClaudeExportStage.Requested or ClaudeExportStage.Running;
    public bool IsDone => Stage == ClaudeExportStage.Done;
    public bool IsFailed => Stage == ClaudeExportStage.Failed;

    /// <summary>Denied or cancelled: the hollow dot.</summary>
    public bool IsEnded => Stage is ClaudeExportStage.Denied or ClaudeExportStage.Cancelled;
    public bool ShowProgress => Stage == ClaudeExportStage.Running;
    public double Progress => _editor.Export.Progress;
    public bool CanCancel => Stage == ClaudeExportStage.Running;
    public bool CanReveal => Stage == ClaudeExportStage.Done;
    public bool CanRetry => Stage == ClaudeExportStage.Failed;

    /// <summary>"Claude wants to export 4 clips (4:47)".</summary>
    public string RequestTitle => Target is { } t
        ? $"Claude wants to export {t.ClipCount} {(t.ClipCount == 1 ? "clip" : "clips")} ({TimeFormat.WholeSeconds(t.Duration)})"
        : "";

    /// <summary>"to D:\Videos\keynote-cut.mp4 · Lossless · MP4".</summary>
    public string RequestDetail => Target switch
    {
        null => "",
        { Merged: true } t => $"to {t.Path} · {t.Mode} · {t.Container}",
        var t => $"to {t.Folder} · {t.Name} · {t.Mode} · {t.Container}",
    };

    public string Title => Stage switch
    {
        ClaudeExportStage.Requested => "Export requested",
        ClaudeExportStage.Running => $"Exporting {Target?.Name}",
        ClaudeExportStage.Done => $"Exported {Target?.Name}",
        ClaudeExportStage.Failed => "Export failed",
        ClaudeExportStage.Denied => "Export denied",
        ClaudeExportStage.Cancelled => "Export cancelled",
        _ => "",
    };

    public string Detail => Stage switch
    {
        ClaudeExportStage.Requested => $"{Target?.Name} · {Target?.ClipsText} · waiting for your answer",
        ClaudeExportStage.Running => RunningDetail,
        ClaudeExportStage.Done => $"{Target?.ClipsText} · {_sizeText} · {Target?.Folder}",
        ClaudeExportStage.Failed => Error ?? "",
        ClaudeExportStage.Denied => $"Claude was told you declined {Target?.Name}.",
        ClaudeExportStage.Cancelled =>
            $"Stopped at {Math.Floor(_stoppedAt * 100).ToString(CultureInfo.InvariantCulture)}%. "
            + (Target?.Merged == false ? "The unfinished files were deleted." : "The unfinished file was deleted."),
        _ => "",
    };

    /// <summary>"Writing clip 3 of 4 · 45% · 5 s left".</summary>
    private string RunningDetail
    {
        get
        {
            var export = _editor.Export;
            string text = $"{export.ProgressText} · {export.PercentText}";
            return export.RemainingSeconds is { } left ? $"{text} · {ExportViewModel.ShortTime(left)} left" : text;
        }
    }

    public string WhenText => !HasCard ? "" : IsLive ? "now" : ClaudeLogItemViewModel.Ago(At, Now);

    /// <summary>Shows the request and waits for Allow, Always allow or Deny. Cancelled when the token is, or when the file closes.</summary>
    public async Task<ClaudeExportAnswer> AskAsync(ClaudeExportTarget target, CancellationToken cancellationToken = default)
    {
        if (IsAsking)
            throw new InvalidOperationException("Claude is already waiting for an answer.");
        var answer = new TaskCompletionSource<ClaudeExportAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answer = answer;
        _next = target;
        Target = target;
        Error = null;
        At = Now = DateTimeOffset.Now;
        Stage = ClaudeExportStage.Requested;
        IsAsking = true;
        _editor.Claude.IsOpen = true;
        // The token may be cancelled on a pipe thread.
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => Withdraw(answer)));
        return await answer.Task.ConfigureAwait(true);
    }

    /// <summary>The next export Claude starts is shown as <paramref name="target"/> (demo, retry).</summary>
    public void Expect(ClaudeExportTarget target) => _next = target;

    /// <summary>Shows a failed export (demo screen).</summary>
    public void ShowFailed(ClaudeExportTarget target, string error, DateTimeOffset at)
    {
        CancelPending();
        _next = null;
        Target = target;
        Error = error;
        At = at;
        Now = DateTimeOffset.Now;
        Stage = ClaudeExportStage.Failed;
    }

    /// <summary>Withdraws a pending request and removes the card.</summary>
    public void Clear()
    {
        CancelPending();
        Stage = ClaudeExportStage.None;
        Target = null;
        _next = null;
        Error = null;
    }

    [RelayCommand]
    private void Allow() => Answer(ClaudeExportAnswer.Allow);

    /// <summary>Allows this export and every later one (Settings → MCP server → Export: Allow).</summary>
    [RelayCommand]
    private void AlwaysAllow()
    {
        if (_answer is null)
            return;
        _editor.Settings.ExportPermission = McpPermission.Allow;
        Answer(ClaudeExportAnswer.AlwaysAllow);
    }

    [RelayCommand]
    private void Deny() => Answer(ClaudeExportAnswer.Deny);

    /// <summary>Stops the export; the card follows the export to Cancelled.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (CanCancel)
            _editor.Export.CancelExport();
    }

    /// <summary>Runs the failed export again with the same target.</summary>
    [RelayCommand]
    private void Retry()
    {
        if (!CanRetry)
            return;
        _next = Target;
        if (_editor.Export.RetryForClaude() is { } refused)
        {
            _next = null;
            _editor.ShowMessage(refused);
        }
    }

    [RelayCommand]
    private void Reveal()
    {
        if (CanReveal && _revealPath is { } path && !_editor.Export.IsSimulated)
            FileManager.Reveal(path);
    }

    private void Answer(ClaudeExportAnswer answer)
    {
        if (_answer is not { } pending)
            return;
        _answer = null;
        IsAsking = false;
        if (answer == ClaudeExportAnswer.Deny)
        {
            _next = null;
            At = Now = DateTimeOffset.Now;
            Stage = ClaudeExportStage.Denied;
        }
        // Allow stays Requested until the export runs; the host clears the card if it cannot start.
        pending.TrySetResult(answer);
    }

    private void Withdraw(TaskCompletionSource<ClaudeExportAnswer> answer)
    {
        if (!ReferenceEquals(_answer, answer))
            return;
        _answer = null;
        IsAsking = false;
        _next = null;
        if (Stage == ClaudeExportStage.Requested)
        {
            Stage = ClaudeExportStage.None;
            Target = null;
        }
        answer.TrySetCanceled();
    }

    private void CancelPending()
    {
        if (_answer is { } pending)
            Withdraw(pending);
    }

    private void OnExportChanged(object? sender, PropertyChangedEventArgs e)
    {
        var export = _editor.Export;
        switch (e.PropertyName)
        {
            case nameof(ExportViewModel.Outcome):
                if (export.IsByClaude && export.Outcome == ExportOutcome.Running)
                    Begin();
                else if (Stage == ClaudeExportStage.Running)
                    Finish(export.Outcome);
                break;
            case nameof(ExportViewModel.Progress) or nameof(ExportViewModel.ProgressText) or nameof(ExportViewModel.Stats)
                or nameof(ExportViewModel.OutputPath) when Stage == ClaudeExportStage.Running:
                RefreshName();
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(Detail));
                break;
        }
    }

    private void Begin()
    {
        CancelPending();
        Target = _next ?? _editor.Export.ClaudeTarget();
        _next = null;
        Error = null;
        RefreshName();
        At = Now = DateTimeOffset.Now;
        Stage = ClaudeExportStage.Running;
        _editor.Claude.IsOpen = true;
    }

    /// <summary>Snapshots what the card shows, so the user's next export leaves it alone.</summary>
    private void Finish(ExportOutcome outcome)
    {
        var export = _editor.Export;
        switch (outcome)
        {
            case ExportOutcome.Done:
                RefreshName();
                var files = export.OutputFiles;
                if (!export.IsSimulated && files.Count > 0 && Target is { } target)
                    Target = target with { Folder = System.IO.Path.GetDirectoryName(files[0]) ?? target.Folder };
                _revealPath = files.Count > 0 ? files[0] : Target?.Path;
                _sizeText = export.OutputSizeText;
                At = Now = DateTimeOffset.Now;
                Stage = ClaudeExportStage.Done;
                break;
            case ExportOutcome.Failed:
                Error = export.ErrorText;
                At = Now = DateTimeOffset.Now;
                Stage = ClaudeExportStage.Failed;
                break;
            case ExportOutcome.Cancelled:
                _stoppedAt = export.Progress;
                At = Now = DateTimeOffset.Now;
                Stage = ClaudeExportStage.Cancelled;
                break;
        }
    }

    /// <summary>The planner may have added " (2)" to the name of a real export.</summary>
    private void RefreshName()
    {
        var export = _editor.Export;
        if (Target is not { } target || export.IsSimulated || export.OutputFiles is not { Count: > 0 } files)
            return;
        Target = target with
        {
            Name = files.Count == 1 ? System.IO.Path.GetFileName(files[0]) : $"{files.Count} files",
            Path = files.Count == 1 ? files[0] : target.Path,
        };
    }
}
