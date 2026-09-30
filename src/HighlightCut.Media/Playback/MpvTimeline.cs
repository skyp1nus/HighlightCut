using System.Globalization;
using System.Text;

namespace HighlightCut.Media.Playback;

/// <summary>A file on the timeline mpv plays: its path, how long it lasts there and how many audio tracks it has.</summary>
public readonly record struct TimelineFile(string Path, double Duration, int AudioTracks);

/// <summary>
/// Several files end to end as one thing for mpv to play: an <c>edl://</c> path. mpv's timeline demuxer then plays
/// across the joins itself (a few tens of milliseconds where the decoders change), and positions and seeks are seconds
/// on the whole timeline. Each file lasts exactly its given duration, so mpv's times are the project's. The audio
/// tracks are laid out like the file with the most of them (<c>layout=this</c>): track N of each file is mpv's
/// <c>aidN</c>, and a file without it plays nothing there.
/// </summary>
public static class MpvTimeline
{
    /// <summary>What to load: the file itself when there is one, else an <c>edl://</c> path.</summary>
    public static string PathFor(IReadOnlyList<TimelineFile> files)
    {
        if (files.Count == 0)
            throw new ArgumentException("A timeline needs at least one file.", nameof(files));
        if (files.Count == 1)
            return files[0].Path;
        int layout = 0;
        for (int i = 1; i < files.Count; i++)
        {
            if (files[i].AudioTracks > files[layout].AudioTracks)
                layout = i;
        }
        var edl = new StringBuilder("edl://");
        for (int i = 0; i < files.Count; i++)
        {
            if (i > 0)
                edl.Append(';');
            // %n% gives the path's length in bytes, so commas, semicolons and anything else in it are read as they are.
            edl.Append('%').Append(Encoding.UTF8.GetByteCount(files[i].Path).ToString(CultureInfo.InvariantCulture)).Append('%')
                .Append(files[i].Path)
                .Append(",start=0,length=").Append(files[i].Duration.ToString("0.######", CultureInfo.InvariantCulture));
            if (i == layout && layout > 0)
                edl.Append(",layout=this");
        }
        return edl.ToString();
    }
}
