using System.Globalization;
using HighlightCut.App.Services;
using HighlightCut.Core.Time;

namespace HighlightCut.App.ViewModels;

/// <param name="Path">The file to open; empty for the design's sample entries.</param>
public sealed record RecentFileViewModel(string Name, string Path, string DurationText, string WhenText)
{
    public static RecentFileViewModel From(RecentFile file, DateTime now)
    {
        var opened = file.OpenedUtc.ToLocalTime();
        string when = opened.Date == now.Date ? "Today"
            : opened.Date == now.Date.AddDays(-1) ? "Yesterday"
            : opened.ToString(opened.Year == now.Year ? "MMM d" : "MMM d, yyyy", CultureInfo.InvariantCulture);
        return new RecentFileViewModel(System.IO.Path.GetFileName(file.Path), file.Path, TimeFormat.WholeSeconds(file.Duration), when);
    }
}
