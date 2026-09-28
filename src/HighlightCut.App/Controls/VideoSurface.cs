using System.Globalization;
using Avalonia;
using Avalonia.Media;
using HighlightCut.App.Services;

namespace HighlightCut.App.Controls;

/// <summary>
/// The player picture. Until libmpv playback is connected it shows the nearest thumbnail of a real
/// file, or for the design's sample the shaded placeholder with "source frame" and the timecode.
/// </summary>
public sealed class VideoSurface : Avalonia.Controls.Control
{
    public static readonly StyledProperty<IMediaPreview?> MediaProperty =
        AvaloniaProperty.Register<VideoSurface, IMediaPreview?>(nameof(Media));

    public static readonly StyledProperty<double> TimeProperty =
        AvaloniaProperty.Register<VideoSurface, double>(nameof(Time));

    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<VideoSurface, string?>(nameof(Caption));

    private static readonly IBrush HatchBrush = new SolidColorBrush(Color.FromArgb(7, 255, 255, 255));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromArgb(107, 255, 255, 255));

    static VideoSurface() => AffectsRender<VideoSurface>(MediaProperty, TimeProperty, CaptionProperty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MediaProperty)
        {
            if (change.GetOldValue<IMediaPreview?>() is { } old)
                old.Changed -= OnMediaChanged;
            if (change.GetNewValue<IMediaPreview?>() is { } media)
                media.Changed += OnMediaChanged;
        }
    }

    private void OnMediaChanged(object? sender, EventArgs e) => InvalidateVisual();

    public IMediaPreview? Media { get => GetValue(MediaProperty); set => SetValue(MediaProperty, value); }
    public double Time { get => GetValue(TimeProperty); set => SetValue(TimeProperty, value); }
    public string? Caption { get => GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (Media is null)
        {
            context.FillRectangle(Brushes.Black, rect);
            return;
        }
        Media.DrawFrame(context, rect, Time, FrameLook.Player, 0);
        if (!Media.IsPlaceholder)
            return;
        Hatch.Draw(context, rect, rect, HatchBrush, 12);

        var font = new Typeface(new FontFamily(FontSetup.Mono));
        var lines = new[] { "source frame", Caption ?? "" };
        double lineHeight = 11 * 1.4, gap = 4;
        double total = lines.Length * lineHeight + gap;
        double y = (rect.Height - total) / 2;
        foreach (var line in lines)
        {
            var text = new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, font, 11, TextBrush);
            context.DrawText(text, new Point((rect.Width - text.Width) / 2, y + (lineHeight - text.Height) / 2));
            y += lineHeight + gap;
        }
    }
}
