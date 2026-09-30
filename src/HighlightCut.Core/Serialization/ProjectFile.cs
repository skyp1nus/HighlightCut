using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HighlightCut.Core.Model;

namespace HighlightCut.Core.Serialization;

/// <summary>A project file that cannot be read.</summary>
public sealed class ProjectFileException : Exception
{
    public ProjectFileException()
    {
    }

    public ProjectFileException(string message)
        : base(message)
    {
    }

    public ProjectFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Reads and writes <c>.highlightcut.json</c> project files. Each video's path is stored relative to the
/// project file when possible, so a project folder can be moved together with its videos. Version 1 files (one
/// <c>"source"</c>) open as a project with one video; saving writes version 2 (<c>"sources"</c>). Projects saved before
/// the app was renamed (<c>.ourcut.json</c>, format "ourcut-project") open the same way.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".highlightcut.json";
    public const string FormatName = "highlightcut-project";

    /// <summary>The extension from before the rename. Such files still open, and saving one keeps its name.</summary>
    public const string LegacyExtension = ".ourcut.json";
    public const string LegacyFormatName = "ourcut-project";

    /// <summary>Whether a path names a project file (either extension) rather than a video.</summary>
    public static bool IsProjectPath(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(LegacyExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Current schema version. Files with a higher version are rejected. Version 2 has a list of videos
    /// (<c>"sources"</c>, each with an id) and each clip names its video (<c>"source"</c>); version 1 had one video.
    /// </summary>
    public const int CurrentVersion = 2;

    public static string Serialize(Project project, string? projectPath = null)
    {
        string? baseDir = projectPath is null ? null : Path.GetDirectoryName(Path.GetFullPath(projectPath));
        var dto = new ProjectDto
        {
            Format = FormatName,
            Version = CurrentVersion,
            Name = project.Name,
            Sources = [.. project.Sources.Select(s => new SourceDto
            {
                Id = s.Id,
                Path = StorePath(s.Path, baseDir),
                Duration = s.Duration,
                FrameRate = s.FrameRate,
                AudioStreams = [.. s.AudioTracks.Select(a => AudioStream(a, project.MixOf(s.Id, a.Index)))],
            })],
            LastSourceId = project.NextSourceId - 1,
            LastClipId = project.NextClipId - 1,
            Clips = [.. project.Clips.Select(c => new ClipDto
            {
                Id = c.Id, Source = c.SourceId, Label = c.Label, Start = c.Start, End = c.End, Included = c.IsIncluded,
                Color = ClipPalette.Key(c.Color),
            })],
        };
        return JsonSerializer.Serialize(dto, ProjectJsonContext.Default.ProjectDto);
    }

    /// <param name="projectPath">Where the file lives; relative source paths are resolved against its folder.</param>
    /// <exception cref="ProjectFileException">The text is not a valid HighlightCut project.</exception>
    public static Project Deserialize(string json, string? projectPath = null)
    {
        ProjectDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, ProjectJsonContext.Default.ProjectDto);
        }
        catch (JsonException e)
        {
            throw new ProjectFileException($"Not a valid project file: {e.Message}", e);
        }

        if (dto is null || dto.Format is not (FormatName or LegacyFormatName))
            throw new ProjectFileException("Not a HighlightCut project file.");
        if (dto.Version > CurrentVersion)
            throw new ProjectFileException($"This project was saved by a newer version of HighlightCut (format {dto.Version}).");
        if (dto.Version < 1)
            throw new ProjectFileException($"Unknown project format version {dto.Version}.");

        string? baseDir = projectPath is null ? null : Path.GetDirectoryName(Path.GetFullPath(projectPath));
        // Version 1 has one video, "source", which becomes video 1.
        var sourceDtos = dto.Version >= 2 ? dto.Sources ?? []
            : dto.Source is { } single ? [single.WithId(SourceMedia.FirstId)] : [];
        var sources = new List<SourceMedia>();
        foreach (var s in sourceDtos)
        {
            if (string.IsNullOrWhiteSpace(s.Path))
                throw new ProjectFileException("A video of the project has no path.");
            if (!(s.Duration >= 0) || double.IsInfinity(s.Duration))
                throw new ProjectFileException($"The duration of {Path.GetFileName(s.Path)} is invalid.");
            if (s.Id is not { } id || id < 1)
                throw new ProjectFileException($"{Path.GetFileName(s.Path)} has no valid id.");
            if (sources.Exists(x => x.Id == id))
                throw new ProjectFileException($"Video id {id} appears twice.");
            sources.Add(new SourceMedia(ResolvePath(s.Path, baseDir), s.Duration, s.FrameRate,
                [.. (s.AudioStreams ?? []).Select(a => new AudioTrack(a.Index, a.Label ?? $"Audio {a.Index}"))], id));
        }

        // Files saved before clip names were unique can have the same name twice: the first clip keeps it and the
        // others get " · 2", " · 3"…. Files saved before clips had colours (or with a colour this version does not
        // know) get them as new clips would.
        var clips = new List<Clip>();
        var ids = new HashSet<int>();
        var uncoloured = new HashSet<int>();
        foreach (var c in dto.Clips ?? [])
        {
            if (!ids.Add(c.Id))
                throw new ProjectFileException($"Clip id {c.Id} appears twice.");
            if (!(c.End > c.Start) || c.Start < 0 || double.IsInfinity(c.End))
                throw new ProjectFileException($"Clip {c.Id} has an invalid range ({c.Start}–{c.End}).");
            // Version 1 clips are all in its one video; a version 2 clip may leave its video out while there is one.
            int sourceId = dto.Version >= 2 && c.Source is { } id ? id
                : sources.Count > 1 ? throw new ProjectFileException($"Clip {c.Id} does not say which video it is in.")
                : sources.FirstOrDefault()?.Id ?? SourceMedia.FirstId;
            if (sources.Count > 0 && !sources.Exists(s => s.Id == sourceId))
                throw new ProjectFileException($"Clip {c.Id} is in video {sourceId}, which the project does not have.");
            string label = ClipNames.Unique(clips, string.IsNullOrWhiteSpace(c.Label) ? ClipNames.Default(c.Id) : c.Label);
            if (!ClipPalette.TryParse(c.Color, out var color))
                uncoloured.Add(c.Id);
            clips.Add(new Clip(c.Id, label, c.Start, c.End, c.Included, color, sourceId));
        }

        // Files saved before tracks had a volume have none: every track plays at 0 dB, unmuted.
        var project = new Project(string.IsNullOrWhiteSpace(dto.Name) ? "Untitled project" : dto.Name, [.. sources],
            [.. ClipPalette.Fill(clips, uncoloured)]) { LastClipId = Math.Max(0, dto.LastClipId ?? 0), LastSourceId = Math.Max(0, dto.LastSourceId ?? 0) };
        project = project.WithClips(project.Clips).WithSources(project.Sources);
        foreach (var (s, id) in sourceDtos.Select((s, i) => (s, sources[i].Id)))
        {
            foreach (var a in s.AudioStreams ?? [])
                project = project.WithMix(new TrackMix(a.Index, TrackMix.ClampGain(a.GainDb ?? 0), a.Muted ?? false, id));
        }
        return project;
    }

    /// <summary>Writes the project atomically (temporary file, then replace).</summary>
    public static async Task SaveAsync(Project project, string path, CancellationToken cancellationToken = default)
    {
        string full = Path.GetFullPath(path);
        string json = Serialize(project, full);
        string temp = full + ".tmp";
        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, full, overwrite: true);
    }

    /// <exception cref="ProjectFileException">The file is not a valid HighlightCut project.</exception>
    public static async Task<Project> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Deserialize(json, path);
    }

    /// <summary>Suggested file name for a project, e.g. "launch-keynote.highlightcut.json".</summary>
    public static string FileNameFor(Project project)
    {
        string name = string.Concat(project.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
        return (name.Length == 0 ? "project" : name) + Extension;
    }

    /// <summary>Project name for a file, e.g. "launch-keynote" for "launch-keynote.highlightcut.json".</summary>
    public static string NameFromPath(string path)
    {
        string file = Path.GetFileName(path);
        return file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? file[..^Extension.Length]
            : file.EndsWith(LegacyExtension, StringComparison.OrdinalIgnoreCase) ? file[..^LegacyExtension.Length]
            : Path.GetFileNameWithoutExtension(file);
    }

    /// <summary>A track with its mix; the defaults (0 dB, unmuted) are left out of the file.</summary>
    private static AudioStreamDto AudioStream(AudioTrack track, TrackMix mix) => new()
    {
        Index = track.Index,
        Label = track.Label,
        GainDb = mix.GainDb == 0 ? null : mix.GainDb,
        Muted = mix.IsMuted ? true : null,
    };

    private static string StorePath(string sourcePath, string? baseDir)
    {
        if (baseDir is null || !Path.IsPathFullyQualified(sourcePath))
            return sourcePath;
        string relative = Path.GetRelativePath(baseDir, sourcePath);
        // Different drive (Windows) or unrelated root: keep it absolute.
        if (Path.IsPathRooted(relative))
            return sourcePath;
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string ResolvePath(string stored, string? baseDir)
    {
        if (Path.IsPathFullyQualified(stored) || baseDir is null)
            return stored;
        string native = stored.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(baseDir, native));
    }
}

internal sealed class ProjectDto
{
    public string? Format { get; set; }
    public int Version { get; set; }
    public string? Name { get; set; }

    /// <summary>The one video of a version 1 file; version 2 writes <see cref="Sources"/>.</summary>
    public SourceDto? Source { get; set; }

    /// <summary>The videos in timeline order (version 2).</summary>
    public List<SourceDto>? Sources { get; set; }

    /// <summary>The highest video id handed out (<see cref="Project.LastSourceId"/>); absent in version 1.</summary>
    public int? LastSourceId { get; set; }

    /// <summary>The highest clip id handed out (<see cref="Project.LastClipId"/>); absent in older files.</summary>
    public int? LastClipId { get; set; }

    public List<ClipDto>? Clips { get; set; }
}

internal sealed class SourceDto
{
    /// <summary>The video's <see cref="SourceMedia.Id"/>; absent in version 1, whose one video is video 1.</summary>
    public int? Id { get; set; }

    public string? Path { get; set; }
    public double Duration { get; set; }
    public double FrameRate { get; set; }
    public List<AudioStreamDto>? AudioStreams { get; set; }

    public SourceDto WithId(int id) => new() { Id = id, Path = Path, Duration = Duration, FrameRate = FrameRate, AudioStreams = AudioStreams };
}

internal sealed class AudioStreamDto
{
    public int Index { get; set; }
    public string? Label { get; set; }

    /// <summary>Volume in dB; absent for 0 dB.</summary>
    public double? GainDb { get; set; }

    /// <summary>Absent when the track is not muted.</summary>
    public bool? Muted { get; set; }
}

internal sealed class ClipDto
{
    public int Id { get; set; }

    /// <summary>The id of the clip's video (version 2).</summary>
    public int? Source { get; set; }

    public string? Label { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public bool Included { get; set; } = true;

    /// <summary>A <see cref="ClipColor"/> name, e.g. "teal"; absent in older files.</summary>
    public string? Color { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ProjectDto))]
internal sealed partial class ProjectJsonContext : JsonSerializerContext;
