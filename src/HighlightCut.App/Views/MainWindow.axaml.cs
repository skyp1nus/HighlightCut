using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HighlightCut.App.ViewModels;

namespace HighlightCut.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Shortcuts are handled before focused controls (buttons, sliders) can swallow Space or the arrows.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) =>
        {
            Player.SetDropHover(false);
            Timeline.SetDropHover(false);
        });
    }

    private EditorViewModel? Editor => DataContext as EditorViewModel;

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Editor is not { } editor)
            return;
        // Recording a shortcut takes every key, even from a text box.
        if (FocusManager?.GetFocusedElement() is TextBox && !editor.Settings.Keyboard.IsRecording)
            return;
        e.Handled = Shortcuts.Handle(editor, e.Key, e.KeyModifiers);
    }

    /// <summary>Files dropped onto the timeline of an open project are added after its last video; anywhere else they open.</summary>
    private bool OverTimeline(DragEventArgs e) => Editor is { HasFile: true } && Timeline.IsOverTracks(e.GetPosition(Timeline));

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        bool files = e.DragEffects != DragDropEffects.None;
        bool timeline = files && OverTimeline(e);
        Player.SetDropHover(files && !timeline);
        Timeline.SetDropHover(timeline);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        bool timeline = OverTimeline(e);
        Player.SetDropHover(false);
        Timeline.SetDropHover(false);
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        if (paths.Count == 0 || Editor is not { } editor)
            return;
        List<string> videos = [.. paths.Where(p => !Core.Serialization.ProjectFile.IsProjectPath(p))];
        if (timeline && videos.Count > 0)
            _ = editor.AddVideosAsync(videos);
        else
            _ = editor.OpenPath(paths[0]);
    }
}
