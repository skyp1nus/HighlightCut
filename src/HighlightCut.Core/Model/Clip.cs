namespace HighlightCut.Core.Model;

/// <summary>
/// A kept range of the source file. Times are seconds on the source timeline
/// (0 = first frame, i.e. the container start time already subtracted).
/// </summary>
/// <param name="Id">Stable identifier; survives reordering, undo and save/load.</param>
/// <param name="Label">User-visible name, unique in the project (see <see cref="ClipNames"/>).</param>
/// <param name="Start">In-point, inclusive.</param>
/// <param name="End">Out-point, exclusive.</param>
/// <param name="IsIncluded">Excluded clips stay in the project but are not exported.</param>
/// <param name="Color">Colour of the clip on the timeline and in the clip list (see <see cref="ClipPalette"/>).</param>
public sealed record Clip(int Id, string Label, double Start, double End, bool IsIncluded = true, ClipColor Color = ClipColor.Teal)
{
    public double Duration => End - Start;

    public bool Contains(double time) => time >= Start && time <= End;
}
