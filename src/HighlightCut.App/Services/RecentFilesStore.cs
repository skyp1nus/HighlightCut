using System.Text.Json;
using System.Text.Json.Serialization;

namespace HighlightCut.App.Services;

/// <summary>A recently opened video or project.</summary>
public sealed record RecentFile(string Path, double Duration, DateTime OpenedUtc);

/// <summary>Remembers recently opened files in a small JSON file in the user's app data.</summary>
public sealed class RecentFilesStore(string file)
{
    /// <summary>How many files are kept, so a larger <see cref="Limit"/> shows the older ones again.</summary>
    public const int Capacity = 20;

    /// <summary>How many files the list has: Settings → General → Recent files (5, 10 or 20).</summary>
    public int Limit { get; set; } = 10;

    public static string DefaultFile { get; } = AppDataFolder.PathFor("recent.json");

    public string File { get; } = file;

    /// <summary>Recent files that still exist, newest first, at most <see cref="Limit"/>. A missing or broken list reads as empty.</summary>
    public IReadOnlyList<RecentFile> Load() => [.. LoadAll().Take(Limit)];

    private IReadOnlyList<RecentFile> LoadAll()
    {
        try
        {
            if (!System.IO.File.Exists(File))
                return [];
            var list = JsonSerializer.Deserialize(System.IO.File.ReadAllBytes(File), RecentFilesJson.Default.ListRecentFile) ?? [];
            return [.. list.Where(r => !string.IsNullOrEmpty(r.Path) && System.IO.File.Exists(r.Path)).Take(Capacity)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Add(string path, double duration, DateTime? openedUtc = null)
    {
        string full = System.IO.Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var list = LoadAll().Where(r => !string.Equals(r.Path, full, comparison)).ToList();
        list.Insert(0, new RecentFile(full, duration, openedUtc ?? DateTime.UtcNow));
        Write([.. list.Take(Capacity)]);
    }

    /// <summary>Forgets every file.</summary>
    public void Clear() => Write([]);

    private void Write(List<RecentFile> list)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
            string temp = File + ".tmp";
            System.IO.File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(list, RecentFilesJson.Default.ListRecentFile));
            System.IO.File.Move(temp, File, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the recent list is not worth an error message.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<RecentFile>))]
internal sealed partial class RecentFilesJson : JsonSerializerContext;
