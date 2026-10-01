using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.Core.Model;
using HighlightCut.Core.Time;

namespace HighlightCut.App.ViewModels;

/// <summary>
/// A video in the Videos list: its number on the timeline, name, length and place, with Move up, Move down and Remove.
/// Changes go through <see cref="EditorViewModel"/> as edits; the list is made again after each one.
/// </summary>
public sealed partial class VideoViewModel(EditorViewModel editor, SourceMedia source, int number, int count, double offset, int clipCount)
    : ViewModelBase
{
    public int SourceId { get; } = source.Id;
    public int Number { get; } = number;
    public string Name { get; } = source.FileName;
    public string Path { get; } = source.Path;
    public double Offset { get; } = offset;
    public double Duration { get; } = source.Duration;
    public int ClipCount { get; } = clipCount;

    /// <summary>"12:34".</summary>
    public string DurationText => TimeFormat.WholeSeconds(Duration);

    /// <summary>Where it sits on the timeline: "12:34 – 25:00".</summary>
    public string PlaceText => TimeFormat.WholeSeconds(Offset) + " – " + TimeFormat.WholeSeconds(Offset + Duration);

    /// <summary>"3 clips", "no clips".</summary>
    public string ClipsText => ClipCount switch { 0 => "no clips", 1 => "1 clip", var n => $"{n} clips" };

    public string Tip => $"{Path}\nOn the timeline {PlaceText} · {DurationText} long · {ClipsText}";

    public bool CanMoveUp => Number > 1;
    public bool CanMoveDown => Number < count;

    /// <summary>Row being dragged in the list.</summary>
    [ObservableProperty]
    public partial bool IsDragSource { get; set; }

    /// <summary>Row under the pointer while another row is dragged.</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    [RelayCommand]
    private void MoveUp() => editor.MoveVideo(SourceId, Number - 2);

    [RelayCommand]
    private void MoveDown() => editor.MoveVideo(SourceId, Number);

    /// <summary>Remove was pressed on a video with clips: the row asks whether its clips should go too.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingRemove { get; set; }

    /// <summary>"Remove with its 3 clips?".</summary>
    public string ConfirmText => ClipCount == 1 ? "Remove with its clip?" : $"Remove with its {ClipCount} clips?";

    /// <summary>The ×: removes the video, or with clips first asks (Remove, Cancel).</summary>
    [RelayCommand]
    public void Remove()
    {
        if (ClipCount > 0 && !IsConfirmingRemove)
            IsConfirmingRemove = true;
        else
            editor.RemoveVideo(SourceId);
    }

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;

    [RelayCommand]
    private void GoTo() => editor.GoToVideo(SourceId);
}
