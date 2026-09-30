using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.Core.Model;
using HighlightCut.Core.Time;

namespace HighlightCut.App.ViewModels;

/// <summary>
/// A clip as shown in the list and on the timeline. Its data mirrors a <see cref="Clip"/> of the
/// Core project; changes go through <see cref="EditorViewModel"/> and come back via
/// <see cref="Update"/>.
/// </summary>
public sealed partial class ClipViewModel : ViewModelBase
{
    private readonly Action<ClipViewModel, bool>? _setIncluded;
    private readonly Action<ClipViewModel, ClipColor>? _setColor;

    public ClipViewModel(Clip clip, Action<ClipViewModel, bool>? setIncluded = null, Action<ClipViewModel, ClipColor>? setColor = null,
        Project? project = null)
    {
        Id = clip.Id;
        _setIncluded = setIncluded;
        _setColor = setColor;
        Update(clip, project);
    }

    public int Id { get; }

    [ObservableProperty]
    public partial string Label { get; private set; } = "";

    /// <summary>The video the clip is cut from (<see cref="SourceMedia.Id"/>).</summary>
    [ObservableProperty]
    public partial int SourceId { get; private set; }

    /// <summary>The video's file name when the project has several videos; null with one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVideoName), nameof(VideoSuffix))]
    public partial string? VideoName { get; private set; }

    public bool HasVideoName => VideoName is not null;

    /// <summary>"  ·  part-2.mp4" after the clip's name with several videos; empty with one.</summary>
    public string VideoSuffix => VideoName is null ? "" : "  ·  " + VideoName;

    /// <summary>In-point on the timeline (with several videos, the video's offset plus the clip's time in it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartText), nameof(Duration), nameof(DurationText), nameof(RangeText), nameof(ShortDurationText))]
    public partial double Start { get; private set; }

    /// <summary>Out-point on the timeline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndText), nameof(Duration), nameof(DurationText), nameof(RangeText), nameof(ShortDurationText))]
    public partial double End { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IncludeToggle))]
    public partial bool IsIncluded { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorBrush), nameof(ColorName), nameof(ColorChoices))]
    public partial ClipColor Color { get; private set; }

    public IBrush ColorBrush => ClipBrushes.Solid(Color);

    /// <summary>E.g. "Teal".</summary>
    public string ColorName => Color.ToString();

    /// <summary>The palette for the colour pickers, with this clip's colour marked; picking one is an edit.</summary>
    public IReadOnlyList<ClipColorChoice> ColorChoices =>
        [.. ClipPalette.Colors.Select(c => new ClipColorChoice(c, c == Color, new RelayCommand(() => _setColor?.Invoke(this, c))))];

    /// <summary>Two-way target for the include checkbox; setting it issues an edit.</summary>
    public bool IncludeToggle
    {
        get => IsIncluded;
        set
        {
            if (value != IsIncluded)
                _setIncluded?.Invoke(this, value);
            OnPropertyChanged();
        }
    }

    /// <summary>1-based position in the output order.</summary>
    [ObservableProperty]
    public partial int Number { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Changed by Claude and not undone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAi), nameof(AiTag))]
    public partial bool IsAiChanged { get; set; }

    /// <summary>Claude is working on this clip right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAi), nameof(AiTag))]
    public partial bool IsAiWorking { get; set; }

    /// <summary>Row being dragged in the clip list.</summary>
    [ObservableProperty]
    public partial bool IsDragSource { get; set; }

    /// <summary>Row under the pointer while another row is dragged.</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    public bool IsAi => IsAiChanged || IsAiWorking;
    public string AiTag => IsAiWorking ? "Claude editing" : "Edited";
    public double Duration => End - Start;
    public string StartText => TimeFormat.MinutesSeconds(Start);
    public string EndText => TimeFormat.MinutesSeconds(End);
    public string DurationText => TimeFormat.Duration(Duration);

    /// <summary>In – out as full timecodes, e.g. "00:00:12.000 – 00:00:45.200".</summary>
    public string RangeText => TimeFormat.Timecode(Start) + " – " + TimeFormat.Timecode(End);

    /// <summary>Duration as in the clip list: "33.200 s" or "1:43.440".</summary>
    public string ShortDurationText => TimeFormat.ShortDuration(Duration);

    /// <summary>Changed by Claude's most recent action: gets a thin pulsing blue ring on the timeline.</summary>
    [ObservableProperty]
    public partial bool IsAiRecent { get; set; }

    public bool Contains(double t) => t >= Start && t <= End;

    /// <summary>Copies the Core clip's data, its times placed on <paramref name="project"/>'s timeline.</summary>
    public void Update(Clip clip, Project? project = null)
    {
        var range = project?.FindSource(clip.SourceId) is not null ? project.TimelineRange(clip) : new TimeRange(clip.Start, clip.End);
        Label = clip.Label;
        SourceId = clip.SourceId;
        VideoName = project is { Sources.Count: > 1 } ? project.FindSource(clip.SourceId)?.FileName : null;
        Start = range.Start;
        End = range.End;
        IsIncluded = clip.IsIncluded;
        Color = clip.Color;
    }
}

/// <summary>One colour in a clip's colour picker.</summary>
public sealed record ClipColorChoice(ClipColor Color, bool IsCurrent, ICommand Pick)
{
    public string Name => Color.ToString();
    public IBrush Brush => ClipBrushes.Solid(Color);
}
