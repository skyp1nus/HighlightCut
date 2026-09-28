namespace HighlightCut.App.Services;

/// <summary>What <see cref="AppDataFolder.MoveLegacy"/> did: the entries moved, kept in the old folder, or not movable.</summary>
public sealed record LegacyMoveResult(IReadOnlyList<string> Moved, IReadOnlyList<string> Kept, IReadOnlyList<string> Failed)
{
    public static LegacyMoveResult None { get; } = new([], [], []);

    /// <summary>There was an old folder to move from.</summary>
    public bool Found => Moved.Count + Kept.Count + Failed.Count > 0;
}

/// <summary>
/// The app's folder in the user's local app data (<c>%LOCALAPPDATA%\HighlightCut</c>, <c>~/.local/share/HighlightCut</c>
/// on Linux): settings, the recent files list, models, the preview cache and logs. Before the app was renamed it was
/// <c>OurCut</c>; <see cref="MoveLegacy"/> moves that folder over once, and <see cref="PathFor"/> keeps using the old
/// place for anything that could not be moved.
/// </summary>
public static class AppDataFolder
{
    public const string Name = "HighlightCut";
    public const string LegacyName = "OurCut";

    /// <summary>Left in the new folder after a move, so Settings → MCP server can say that Claude needs the new name.</summary>
    public const string MovedMarker = "moved-from-OurCut.txt";

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string Root => Path.Combine(LocalAppData, Name);

    /// <summary>A file or folder in the app's folder: the new place, or the old one when it is only there.</summary>
    public static string PathFor(string entry) => PathFor(LocalAppData, entry);

    internal static string PathFor(string parent, string entry)
    {
        string current = Path.Combine(parent, Name, entry);
        string legacy = Path.Combine(parent, LegacyName, entry);
        return !Exists(current) && Exists(legacy) ? legacy : current;
    }

    /// <summary>The folder was moved from the old name (now or on an earlier start).</summary>
    public static bool MovedFromLegacy => File.Exists(Path.Combine(Root, MovedMarker));

    /// <summary>A path in the old folder (a models folder saved in the settings, say) at its new place once it has moved.</summary>
    public static string Relocate(string path) => Relocate(LocalAppData, path);

    internal static string Relocate(string parent, string path)
    {
        string legacy = Path.Combine(parent, LegacyName);
        string relative = Path.GetRelativePath(legacy, path);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return path;
        string current = Path.Combine(parent, Name, relative);
        return !Exists(path) && Exists(current) ? current : path;
    }

    /// <summary>What <see cref="MoveLegacy()"/> did at this start, or null if it did not run.</summary>
    public static LegacyMoveResult? MovedAtStart { get; private set; }

    /// <inheritdoc cref="MoveLegacy(string)"/>
    public static LegacyMoveResult MoveLegacy() => MovedAtStart = MoveLegacy(LocalAppData);

    /// <summary>
    /// Moves the old <c>OurCut</c> folder in <paramref name="parent"/> to <c>HighlightCut</c>. Moved, not copied: the
    /// models can be gigabytes. Nothing in the new folder is overwritten; an entry that is in both stays in the old
    /// folder, and so does one that cannot be moved (a file in use), which <see cref="PathFor"/> then still finds there.
    /// </summary>
    internal static LegacyMoveResult MoveLegacy(string parent)
    {
        string from = Path.Combine(parent, LegacyName);
        string to = Path.Combine(parent, Name);
        if (!Directory.Exists(from))
            return LegacyMoveResult.None;

        List<string> moved = [], kept = [], failed = [];
        if (!Directory.Exists(to) && TryMove(from, to))
        {
            moved.Add(from);
        }
        else
        {
            Directory.CreateDirectory(to);
            // Top-level entries (settings.json, models, cache…) move whole. A folder that is in both places gets the
            // entries only the old one has (one model, one cached video each), so nothing is half moved.
            foreach (string entry in Directory.EnumerateFileSystemEntries(from))
            {
                string target = Path.Combine(to, Path.GetFileName(entry));
                if (!Exists(target))
                    (TryMove(entry, target) ? moved : failed).Add(entry);
                else if (Directory.Exists(entry) && Directory.Exists(target))
                    MoveMissing(entry, target, moved, kept, failed);
                else
                    kept.Add(entry);
            }
            DeleteIfEmpty(from);
        }
        try
        {
            File.WriteAllText(Path.Combine(to, MovedMarker),
                $"HighlightCut was called OurCut before. This folder was moved from {from} on {DateTime.Now:yyyy-MM-dd}.\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return new(moved, kept, failed);
    }

    private static void MoveMissing(string from, string to, List<string> moved, List<string> kept, List<string> failed)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(from))
        {
            string target = Path.Combine(to, Path.GetFileName(entry));
            if (Exists(target))
                kept.Add(entry);
            else
                (TryMove(entry, target) ? moved : failed).Add(entry);
        }
        DeleteIfEmpty(from);
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            if (Directory.Exists(from))
                Directory.Move(from, to);
            else
                File.Move(from, to);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteIfEmpty(string folder)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
