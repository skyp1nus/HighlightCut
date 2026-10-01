using System.Collections.Immutable;
using HighlightCut.Core.Model;

namespace HighlightCut.Media.Probing;

/// <summary>How the container seeks, which decides where a stream-copy cut really starts.</summary>
public enum ContainerFamily
{
    /// <summary>MP4/MOV: seeks by presentation time.</summary>
    Mov,

    /// <summary>Matroska/WebM and other indexed containers.</summary>
    Matroska,

    /// <summary>MPEG transport/program streams: no index.</summary>
    MpegTs,

    Other,
}

public sealed record VideoStreamInfo(
    int Index,
    string Codec,
    int Width,
    int Height,
    double FrameRate,
    string FrameRateText,
    bool HasBFrames,
    string? PixelFormat,
    int Rotation)
{
    /// <summary>Width and height as displayed (after rotation).</summary>
    public (int Width, int Height) DisplaySize => Math.Abs(Rotation) % 180 == 90 ? (Height, Width) : (Width, Height);

    public double FrameDuration => FrameRate > 0 ? 1 / FrameRate : 1 / 30.0;

    /// <summary>The frame rate as ffprobe gives it, e.g. "30000/1001"; what a re-encode of several videos keeps.</summary>
    public string? FrameRateRational { get; init; }

    /// <summary>The stream's time base, e.g. "1/15360". Lossless joins need the same in every video.</summary>
    public string? TimeBase { get; init; }
}

/// <param name="Index">Stream index in the container.</param>
/// <param name="Position">Position among the audio streams (ffmpeg's <c>0:a:N</c>).</param>
public sealed record AudioStreamInfo(int Index, int Position, string Codec, int Channels, int SampleRate, string? Title, string? Language)
{
    public string Label => !string.IsNullOrWhiteSpace(Title) ? Title!
        : !string.IsNullOrWhiteSpace(Language) && Language != "und" ? Language!.ToUpperInvariant()
        : $"Audio {Position + 1}";

    /// <summary>ffprobe's channel layout, e.g. "stereo"; null when the file does not say.</summary>
    public string? ChannelLayout { get; init; }
}

public sealed record SubtitleStreamInfo(int Index, string Codec, string? Language);

/// <summary>What ffprobe reports about a media file. Times are seconds; start time is already subtracted.</summary>
public sealed record MediaInfo(
    string Path,
    string FormatName,
    double Duration,
    double StartTime,
    long BitRate,
    long Size,
    VideoStreamInfo? Video,
    ImmutableArray<AudioStreamInfo> Audio,
    ImmutableArray<SubtitleStreamInfo> Subtitles)
{
    public ContainerFamily Family => FamilyOf(FormatName);

    /// <summary>File extension that suits the container, e.g. "mp4" or "mkv".</summary>
    public string NaturalExtension => Family switch
    {
        ContainerFamily.Mov => System.IO.Path.GetExtension(Path).Equals(".mov", StringComparison.OrdinalIgnoreCase) ? "mov" : "mp4",
        ContainerFamily.Matroska => System.IO.Path.GetExtension(Path).Equals(".webm", StringComparison.OrdinalIgnoreCase) ? "webm" : "mkv",
        _ => System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant(),
    };

    /// <summary>The Core description of this file for a project.</summary>
    public SourceMedia ToSourceMedia() =>
        new(Path, Duration, Video?.FrameRate ?? 0, [.. Audio.Select(a => new AudioTrack(a.Index, a.Label))]);

    public static ContainerFamily FamilyOf(string formatName)
    {
        var names = formatName.Split(',');
        if (names.Contains("mov") || names.Contains("mp4"))
            return ContainerFamily.Mov;
        if (names.Contains("matroska") || names.Contains("webm"))
            return ContainerFamily.Matroska;
        if (names.Contains("mpegts") || names.Contains("mpeg") || names.Contains("mpegvideo"))
            return ContainerFamily.MpegTs;
        return ContainerFamily.Other;
    }

    /// <summary>Short description for the title bar, e.g. "keynote.mp4 · 4K · 29.97 fps".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { System.IO.Path.GetFileName(Path) };
            if (Video is { } v)
            {
                var (w, h) = v.DisplaySize;
                int shortSide = Math.Min(w, h);
                parts.Add(shortSide >= 2160 ? "4K" : shortSide >= 1440 ? "1440p" : shortSide >= 1080 ? "1080p"
                    : shortSide >= 720 ? "720p" : $"{w}×{h}");
                parts.Add(v.FrameRateText + " fps");
            }
            else if (Audio.Length > 0)
            {
                parts.Add("audio only");
            }
            return string.Join(" · ", parts);
        }
    }
}
