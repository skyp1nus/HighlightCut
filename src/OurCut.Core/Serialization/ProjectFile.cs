using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OurCut.Core.Model;

namespace OurCut.Core.Serialization;

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
/// Reads and writes <c>.ourcut.json</c> project files. The source path is stored relative to the
/// project file when possible, so a project folder can be moved together with its video.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".ourcut.json";
    public const string FormatName = "ourcut-project";

    /// <summary>Current schema version. Files with a higher version are rejected.</summary>
    public const int CurrentVersion = 1;

    public static string Serialize(Project project, string? projectPath = null)
    {
        string? baseDir = projectPath is null ? null : Path.GetDirectoryName(Path.GetFullPath(projectPath));
        var dto = new ProjectDto
        {
            Format = FormatName,
            Version = CurrentVersion,
            Name = project.Name,
            Source = project.Source is { } s
                ? new SourceDto
                {
                    Path = StorePath(s.Path, baseDir),
                    Duration = s.Duration,
                    FrameRate = s.FrameRate,
                    AudioStreams = [.. s.AudioTracks.Select(a => AudioStream(a, project.MixOf(a.Index)))],
                }
                : null,
            LastClipId = project.NextClipId - 1,
            Clips = [.. project.Clips.Select(c => new ClipDto
            {
                Id = c.Id, Label = c.Label, Start = c.Start, End = c.End, Included = c.IsIncluded, Color = ClipPalette.Key(c.Color),
            })],
        };
        return JsonSerializer.Serialize(dto, ProjectJsonContext.Default.ProjectDto);
    }

    /// <param name="projectPath">Where the file lives; relative source paths are resolved against its folder.</param>
    /// <exception cref="ProjectFileException">The text is not a valid OurCut project.</exception>
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

        if (dto is null || dto.Format != FormatName)
            throw new ProjectFileException("Not an OurCut project file.");
        if (dto.Version > CurrentVersion)
            throw new ProjectFileException($"This project was saved by a newer version of OurCut (format {dto.Version}).");
        if (dto.Version < 1)
            throw new ProjectFileException($"Unknown project format version {dto.Version}.");

        string? baseDir = projectPath is null ? null : Path.GetDirectoryName(Path.GetFullPath(projectPath));
        SourceMedia? source = null;
        if (dto.Source is { } s)
        {
            if (string.IsNullOrWhiteSpace(s.Path))
                throw new ProjectFileException("The project's source file has no path.");
            if (!(s.Duration >= 0) || double.IsInfinity(s.Duration))
                throw new ProjectFileException("The project's source duration is invalid.");
            source = new SourceMedia(ResolvePath(s.Path, baseDir), s.Duration, s.FrameRate,
                [.. (s.AudioStreams ?? []).Select(a => new AudioTrack(a.Index, a.Label ?? $"Audio {a.Index}"))]);
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
            string label = ClipNames.Unique(clips, string.IsNullOrWhiteSpace(c.Label) ? ClipNames.Default(c.Id) : c.Label);
            if (!ClipPalette.TryParse(c.Color, out var color))
                uncoloured.Add(c.Id);
            clips.Add(new Clip(c.Id, label, c.Start, c.End, c.Included, color));
        }

        // Files saved before tracks had a volume have none: every track plays at 0 dB, unmuted.
        var project = new Project(string.IsNullOrWhiteSpace(dto.Name) ? "Untitled project" : dto.Name, source,
            [.. ClipPalette.Fill(clips, uncoloured)]) { LastClipId = Math.Max(0, dto.LastClipId ?? 0) };
        project = project.WithClips(project.Clips);
        foreach (var a in dto.Source?.AudioStreams ?? [])
            project = project.WithMix(new TrackMix(a.Index, TrackMix.ClampGain(a.GainDb ?? 0), a.Muted ?? false));
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

    /// <exception cref="ProjectFileException">The file is not a valid OurCut project.</exception>
    public static async Task<Project> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Deserialize(json, path);
    }

    /// <summary>Suggested file name for a project, e.g. "launch-keynote.ourcut.json".</summary>
    public static string FileNameFor(Project project)
    {
        string name = string.Concat(project.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
        return (name.Length == 0 ? "project" : name) + Extension;
    }

    /// <summary>Project name for a file, e.g. "launch-keynote" for "launch-keynote.ourcut.json".</summary>
    public static string NameFromPath(string path)
    {
        string file = Path.GetFileName(path);
        return file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            ? file[..^Extension.Length]
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
    public SourceDto? Source { get; set; }

    /// <summary>The highest clip id handed out (<see cref="Project.LastClipId"/>); absent in older files.</summary>
    public int? LastClipId { get; set; }

    public List<ClipDto>? Clips { get; set; }
}

internal sealed class SourceDto
{
    public string? Path { get; set; }
    public double Duration { get; set; }
    public double FrameRate { get; set; }
    public List<AudioStreamDto>? AudioStreams { get; set; }
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
