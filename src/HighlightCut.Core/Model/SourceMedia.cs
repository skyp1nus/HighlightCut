using System.Collections.Immutable;

namespace HighlightCut.Core.Model;

/// <summary>An audio stream (track) of the source file, shown as a timeline lane.</summary>
/// <param name="Index">Stream index in the container (as reported by ffprobe).</param>
/// <param name="Label">Name from the stream's title metadata, or "Audio N".</param>
public sealed record AudioTrack(int Index, string Label);

/// <summary>A video file a project cuts from. A project holds one or more, end to end on its timeline.</summary>
/// <param name="Path">Absolute path of the file.</param>
/// <param name="Duration">Length in seconds.</param>
/// <param name="FrameRate">Average frames per second (used for frame stepping).</param>
/// <param name="Id">
/// Stable identifier in its project; survives reordering, undo and save/load. Clips name their video by it. The first
/// video of a project is <see cref="FirstId"/>; <see cref="Editing.Commands.AddSourceCommand"/> hands out the next ones.
/// </param>
public sealed record SourceMedia(string Path, double Duration, double FrameRate, ImmutableArray<AudioTrack> AudioTracks, int Id = SourceMedia.FirstId)
{
    /// <summary>The id of a project's first video, and of every video until a project has several.</summary>
    public const int FirstId = 1;

    public SourceMedia(string path, double duration, double frameRate)
        : this(path, duration, frameRate, [])
    {
    }

    public double FrameDuration => FrameRate > 0 ? 1 / FrameRate : 1 / 30.0;

    /// <summary>The file name without its folder, e.g. "keynote.mp4".</summary>
    public string FileName => System.IO.Path.GetFileName(Path);
}
