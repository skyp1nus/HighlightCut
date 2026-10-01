using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HighlightCut.Media.Analysis;
using HighlightCut.Media.Previews;
using SkiaSharp;

namespace HighlightCut.Media.Caching;

/// <summary>
/// On-disk cache of per-file analysis results (keyframes, waveform, thumbnails, scene scores, transcripts), keyed by path, size
/// and modification time so a changed file is analysed again. Entries are best effort: any read
/// error is treated as a miss.
/// </summary>
public sealed class MediaCache
{
    private const int Version = 1;

    public MediaCache(string? root = null)
    {
        Root = root ?? DefaultRoot;
    }

    /// <summary>Where the app keeps its cache.</summary>
    public static string DefaultRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HighlightCut", "cache");

    public string Root { get; }

    /// <summary>Cache key for a file, or null if it does not exist.</summary>
    public static string? KeyFor(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return null;
        string text = $"{Version}|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();
    }

    private string? DirFor(string mediaPath, bool create)
    {
        string? key = KeyFor(mediaPath);
        if (key is null)
            return null;
        string dir = Path.Combine(Root, key);
        if (create)
            Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- Size and clearing ---------------------------------------------------------------

    /// <summary>The cache's size on disk and how many videos it holds (a folder each). A missing cache is empty.</summary>
    public (long Bytes, int Videos) Measure()
    {
        if (!Directory.Exists(Root))
            return (0, 0);
        long bytes = 0;
        int videos = 0;
        try
        {
            foreach (var file in new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories))
                bytes += Size(file);
            videos = Directory.EnumerateDirectories(Root).Count();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // What could be read so far.
        }
        return (bytes, videos);
    }

    /// <summary>
    /// Frees the cache: every video's analysis goes, except <paramref name="keepMediaPath"/>'s (the open video uses it),
    /// and transcripts stay (they take long to redo). A folder left empty goes too; files in use are skipped.
    /// </summary>
    public void Clear(string? keepMediaPath = null) => Clear(keepMediaPath is null ? [] : [keepMediaPath]);

    /// <summary>As <see cref="Clear(string?)"/>, keeping the folders of several videos (every video of the open project).</summary>
    public void Clear(IReadOnlyCollection<string> keepMediaPaths)
    {
        if (!Directory.Exists(Root))
            return;
        var keep = keepMediaPaths.Select(KeyFor).ToHashSet(StringComparer.Ordinal);
        foreach (string dir in SafeEnumerate(() => Directory.EnumerateDirectories(Root)))
        {
            if (keep.Contains(Path.GetFileName(dir)))
                continue;
            foreach (string entry in SafeEnumerate(() => Directory.EnumerateFileSystemEntries(dir)))
            {
                if (IsTranscript(entry))
                    continue;
                TryDo(() =>
                {
                    if (Directory.Exists(entry))
                        Directory.Delete(entry, recursive: true);
                    else
                        File.Delete(entry);
                });
            }
            TryDo(() =>
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            });
        }
    }

    private static bool IsTranscript(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("transcript-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal) && File.Exists(path);
    }

    private static long Size(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static List<string> SafeEnumerate(Func<IEnumerable<string>> entries)
    {
        try
        {
            return [.. entries()];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ---- Keyframes -----------------------------------------------------------------------

    public double[]? LoadKeyframes(string mediaPath) => Try(() =>
    {
        string? dir = DirFor(mediaPath, create: false);
        if (dir is null || !File.Exists(Path.Combine(dir, "keyframes.bin")))
            return null;
        byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "keyframes.bin"));
        var times = new double[bytes.Length / sizeof(double)];
        Buffer.BlockCopy(bytes, 0, times, 0, times.Length * sizeof(double));
        return times;
    });

    public void SaveKeyframes(string mediaPath, double[] keyframes) => TryDo(() =>
    {
        if (DirFor(mediaPath, create: true) is not { } dir)
            return;
        var bytes = new byte[keyframes.Length * sizeof(double)];
        Buffer.BlockCopy(keyframes, 0, bytes, 0, bytes.Length);
        WriteAtomic(Path.Combine(dir, "keyframes.bin"), bytes);
    });

    // ---- Waveform ------------------------------------------------------------------------

    public WaveformData? LoadWaveform(string mediaPath) => Try(() =>
    {
        string? dir = DirFor(mediaPath, create: false);
        string file = dir is null ? "" : Path.Combine(dir, "waveform.bin");
        if (!File.Exists(file))
            return null;
        using var reader = new BinaryReader(File.OpenRead(file));
        int streams = reader.ReadInt32(), capacity = reader.ReadInt32(), filled = reader.ReadInt32();
        var peaks = new float[streams][];
        for (int s = 0; s < streams; s++)
        {
            peaks[s] = new float[capacity];
            byte[] raw = reader.ReadBytes(capacity * sizeof(float));
            Buffer.BlockCopy(raw, 0, peaks[s], 0, raw.Length);
        }
        return new WaveformData(peaks, filled) { IsComplete = true };
    });

    public void SaveWaveform(string mediaPath, WaveformData data) => TryDo(() =>
    {
        if (!data.IsComplete || DirFor(mediaPath, create: true) is not { } dir)
            return;
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(data.StreamCount);
            writer.Write(data.Capacity);
            writer.Write(data.Filled);
            foreach (var p in data.RawPeaks)
            {
                var raw = new byte[p.Length * sizeof(float)];
                Buffer.BlockCopy(p, 0, raw, 0, raw.Length);
                writer.Write(raw);
            }
        }
        WriteAtomic(Path.Combine(dir, "waveform.bin"), ms.ToArray());
    });

    // ---- Text (e.g. transcripts) ------------------------------------------------------------

    /// <summary>A text file cached for a media file (e.g. a transcript as JSON), or null.</summary>
    /// <param name="name">File name inside the media file's cache folder.</param>
    public string? LoadText(string mediaPath, string name) => Try(() =>
    {
        string? dir = DirFor(mediaPath, create: false);
        string file = dir is null ? "" : Path.Combine(dir, name);
        return File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : null;
    });

    public void SaveText(string mediaPath, string name, string text) => TryDo(() =>
    {
        if (DirFor(mediaPath, create: true) is { } dir)
            WriteAtomic(Path.Combine(dir, name), Encoding.UTF8.GetBytes(text));
    });

    // ---- Scene scores --------------------------------------------------------------------

    public SceneScores? LoadSceneScores(string mediaPath) => Try(() =>
    {
        string? dir = DirFor(mediaPath, create: false);
        string file = dir is null ? "" : Path.Combine(dir, "scenes.bin");
        if (!File.Exists(file))
            return null;
        using var reader = new BinaryReader(File.OpenRead(file));
        double rate = reader.ReadDouble();
        int capacity = reader.ReadInt32(), filled = reader.ReadInt32();
        var scores = new float[capacity];
        byte[] raw = reader.ReadBytes(capacity * sizeof(float));
        if (raw.Length != capacity * sizeof(float))
            return null;
        Buffer.BlockCopy(raw, 0, scores, 0, raw.Length);
        return new SceneScores(rate, scores, filled) { IsComplete = true };
    });

    public void SaveSceneScores(string mediaPath, SceneScores scores) => TryDo(() =>
    {
        if (!scores.IsComplete || DirFor(mediaPath, create: true) is not { } dir)
            return;
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(scores.Rate);
            writer.Write(scores.Capacity);
            writer.Write(scores.Filled);
            var raw = new byte[scores.Capacity * sizeof(float)];
            Buffer.BlockCopy(scores.RawScores, 0, raw, 0, raw.Length);
            writer.Write(raw);
        }
        WriteAtomic(Path.Combine(dir, "scenes.bin"), ms.ToArray());
    });

    // ---- Thumbnails ----------------------------------------------------------------------

    private sealed record ThumbnailIndex(int Width, int Height, int Columns, double[] Times);

    /// <summary>Stores thumbnails as one JPEG atlas (SkiaSharp) plus an index of their times.</summary>
    public void SaveThumbnails(string mediaPath, IReadOnlyList<ThumbnailFrame> frames) => TryDo(() =>
    {
        if (frames.Count == 0 || DirFor(mediaPath, create: true) is not { } dir)
            return;
        int w = frames[0].Width, h = frames[0].Height;
        int columns = Math.Max(1, Math.Min(frames.Count, 16384 / w));
        int rows = (frames.Count + columns - 1) / columns;
        using var atlas = new SKBitmap(new SKImageInfo(columns * w, rows * h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        for (int i = 0; i < frames.Count; i++)
            CopyInto(atlas, frames[i].Bgra, w, h, i % columns * w, i / columns * h);
        using var image = SKImage.FromBitmap(atlas);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 82);
        WriteAtomic(Path.Combine(dir, $"thumbs-{h}.jpg"), jpeg.ToArray());
        var index = new ThumbnailIndex(w, h, columns, [.. frames.Select(f => f.Time)]);
        WriteAtomic(Path.Combine(dir, $"thumbs-{h}.json"), JsonSerializer.SerializeToUtf8Bytes(index));
    });

    public IReadOnlyList<ThumbnailFrame>? LoadThumbnails(string mediaPath, int height) => Try<IReadOnlyList<ThumbnailFrame>?>(() =>
    {
        string? dir = DirFor(mediaPath, create: false);
        if (dir is null)
            return null;
        string jpg = Path.Combine(dir, $"thumbs-{height}.jpg"), json = Path.Combine(dir, $"thumbs-{height}.json");
        if (!File.Exists(jpg) || !File.Exists(json))
            return null;
        var index = JsonSerializer.Deserialize<ThumbnailIndex>(File.ReadAllBytes(json));
        if (index is null || index.Times.Length == 0)
            return null;
        using var decoded = SKBitmap.Decode(jpg);
        if (decoded is null)
            return null;
        // Decoded as BGRA already on the usual platforms: no second copy of the whole atlas.
        using var converted = decoded.ColorType == SKColorType.Bgra8888 ? null : decoded.Copy(SKColorType.Bgra8888);
        var atlas = converted ?? decoded;
        var frames = new List<ThumbnailFrame>(index.Times.Length);
        for (int i = 0; i < index.Times.Length; i++)
        {
            int x = i % index.Columns * index.Width, y = i / index.Columns * index.Height;
            frames.Add(new ThumbnailFrame(index.Times[i], index.Width, index.Height, CopyOut(atlas, x, y, index.Width, index.Height)));
        }
        return frames;
    });

    private static void CopyInto(SKBitmap atlas, byte[] bgra, int w, int h, int x, int y)
    {
        var dst = Pixels(atlas);
        int stride = atlas.RowBytes;
        for (int row = 0; row < h; row++)
            bgra.AsSpan(row * w * 4, w * 4).CopyTo(dst.Slice((y + row) * stride + x * 4, w * 4));
    }

    private static unsafe Span<byte> Pixels(SKBitmap bitmap) => new((void*)bitmap.GetPixels(), bitmap.ByteCount);

    private static byte[] CopyOut(SKBitmap atlas, int x, int y, int w, int h)
    {
        var src = atlas.GetPixelSpan();
        int stride = atlas.RowBytes;
        var bytes = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            src.Slice((y + row) * stride + x * 4, w * 4).CopyTo(bytes.AsSpan(row * w * 4));
        return bytes;
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private static T? Try<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or EndOfStreamException
                                  or ArgumentException or InvalidOperationException)
        {
            return default;
        }
    }

    private static void TryDo(Action write)
    {
        try
        {
            write();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation; failing to write it is not an error.
        }
    }
}
