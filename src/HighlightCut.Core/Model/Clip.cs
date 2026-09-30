namespace HighlightCut.Core.Model;

/// <summary>
/// A kept range of one of the project's videos. Times are seconds on that video
/// (0 = its first frame, i.e. the container start time already subtracted), so a clip moves along with its video when
/// the videos are reordered; <see cref="Project.TimelineRange"/> gives its place on the project's timeline.
/// </summary>
/// <param name="Id">Stable identifier; survives reordering, undo and save/load.</param>
/// <param name="Label">User-visible name, unique in the project (see <see cref="ClipNames"/>).</param>
/// <param name="Start">In-point, inclusive.</param>
/// <param name="End">Out-point, exclusive.</param>
/// <param name="IsIncluded">Excluded clips stay in the project but are not exported.</param>
/// <param name="Color">Colour of the clip on the timeline and in the clip list (see <see cref="ClipPalette"/>).</param>
/// <param name="SourceId">The <see cref="SourceMedia.Id"/> of the video the clip is cut from. A clip never crosses into another video.</param>
public sealed record Clip(int Id, string Label, double Start, double End, bool IsIncluded = true, ClipColor Color = ClipColor.Teal,
    int SourceId = SourceMedia.FirstId)
{
    public double Duration => End - Start;

    public bool Contains(double time) => time >= Start && time <= End;
}
