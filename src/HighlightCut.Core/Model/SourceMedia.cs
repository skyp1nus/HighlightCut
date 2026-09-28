using System.Collections.Immutable;

namespace HighlightCut.Core.Model;

/// <summary>An audio stream (track) of the source file, shown as a timeline lane.</summary>
/// <param name="Index">Stream index in the container (as reported by ffprobe).</param>
/// <param name="Label">Name from the stream's title metadata, or "Audio N".</param>
public sealed record AudioTrack(int Index, string Label);

/// <summary>The video file a project cuts from.</summary>
/// <param name="Path">Absolute path of the file.</param>
/// <param name="Duration">Length in seconds.</param>
/// <param name="FrameRate">Average frames per second (used for frame stepping).</param>
public sealed record SourceMedia(string Path, double Duration, double FrameRate, ImmutableArray<AudioTrack> AudioTracks)
{
    public SourceMedia(string path, double duration, double frameRate)
        : this(path, duration, frameRate, [])
    {
    }

    public double FrameDuration => FrameRate > 0 ? 1 / FrameRate : 1 / 30.0;
}
