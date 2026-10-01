using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.App.Services;

namespace HighlightCut.App.ViewModels;

/// <summary>What the request banner over the preview shows: Claude asks, the user allows or denies.</summary>
public interface IClaudeRequest : INotifyPropertyChanged
{
    /// <summary>The banner is up, waiting for Allow, Always allow or Deny.</summary>
    bool IsAsking { get; }

    string RequestTitle { get; }

    string RequestDetail { get; }

    IRelayCommand AllowCommand { get; }

    IRelayCommand AlwaysAllowCommand { get; }

    IRelayCommand DenyCommand { get; }
}

/// <summary>
/// Claude asks before it opens a file or saves the project when Settings → MCP server says Ask. The banner
/// shows it like an export request; "Always allow" switches the setting to Allow.
/// </summary>
public sealed partial class ClaudeFileRequestViewModel : ViewModelBase, IClaudeRequest
{
    private readonly EditorViewModel _editor;
    private TaskCompletionSource<bool>? _answer;
    private Action? _alwaysAllow;

    public ClaudeFileRequestViewModel(EditorViewModel editor)
    {
        _editor = editor;
        // Another file (opened by the user) makes the request stale: a save would write the wrong project.
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.Media) && _answer is { } pending)
                Withdraw(pending);
        };
    }

    [ObservableProperty]
    public partial bool IsAsking { get; private set; }

    /// <summary>"Claude wants to open talk.mp4".</summary>
    [ObservableProperty]
    public partial string RequestTitle { get; private set; } = "";

    /// <summary>"D:\Videos\talk.mp4 · closes keynote".</summary>
    [ObservableProperty]
    public partial string RequestDetail { get; private set; } = "";

    /// <summary>Asks to open <paramref name="path"/> in place of what is open. True when allowed.</summary>
    public Task<bool> AskToOpenAsync(string path, CancellationToken cancellationToken = default) =>
        AskAsync($"Claude wants to open {Path.GetFileName(path)}",
            _editor.HasFile ? $"{path} · closes {_editor.ProjectName}" : path,
            () => _editor.Settings.OpenFilesPermission = McpPermission.Allow, cancellationToken);

    /// <summary>Asks to add the video at <paramref name="path"/> to the end of the timeline. True when allowed.</summary>
    public Task<bool> AskToAddAsync(string path, CancellationToken cancellationToken = default) =>
        AskAsync($"Claude wants to add {Path.GetFileName(path)}",
            $"{path} · at the end of the timeline, after {_editor.Session.Project.Sources.LastOrDefault()?.FileName ?? _editor.ProjectName}",
            () => _editor.Settings.OpenFilesPermission = McpPermission.Allow, cancellationToken);

    /// <summary>Asks to save the project to <paramref name="path"/>. True when allowed.</summary>
    public Task<bool> AskToSaveAsync(string path, CancellationToken cancellationToken = default) =>
        AskAsync($"Claude wants to save {_editor.ProjectName}",
            !string.Equals(path, _editor.ProjectPath, StringComparison.Ordinal) && File.Exists(path)
                ? $"to {path} · replaces that file"
                : $"to {path}",
            () => _editor.Settings.SaveProjectPermission = McpPermission.Allow, cancellationToken);

    /// <summary>Shows the request and waits for the answer. Cancelled when the token is, or when another file opens.</summary>
    private async Task<bool> AskAsync(string title, string detail, Action alwaysAllow, CancellationToken cancellationToken)
    {
        if (IsAsking)
            throw new InvalidOperationException("Claude is already waiting for an answer.");
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answer = answer;
        _alwaysAllow = alwaysAllow;
        RequestTitle = title;
        RequestDetail = detail;
        IsAsking = true;
        // The token may be cancelled on a pipe thread.
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => Withdraw(answer)));
        return await answer.Task.ConfigureAwait(true);
    }

    [RelayCommand]
    private void Allow() => Answer(true);

    /// <summary>Allows this and every later request of the kind (Settings → MCP server).</summary>
    [RelayCommand]
    private void AlwaysAllow()
    {
        if (_answer is null)
            return;
        _alwaysAllow?.Invoke();
        Answer(true);
    }

    [RelayCommand]
    private void Deny() => Answer(false);

    private void Answer(bool allowed)
    {
        if (_answer is not { } pending)
            return;
        Close();
        pending.TrySetResult(allowed);
    }

    private void Withdraw(TaskCompletionSource<bool> answer)
    {
        if (!ReferenceEquals(_answer, answer))
            return;
        Close();
        answer.TrySetCanceled();
    }

    private void Close()
    {
        _answer = null;
        _alwaysAllow = null;
        IsAsking = false;
    }
}
