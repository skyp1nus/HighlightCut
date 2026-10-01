using System.Text.RegularExpressions;
using HighlightCut.Core.Model;
using HighlightCut.Media.Probing;

namespace HighlightCut.Media.Export;

/// <summary>One included clip as it will be exported.</summary>
/// <param name="Number">1-based position among the exported clips.</param>
/// <param name="Lossless">Cut points for stream copy; null when re-encoding.</param>
/// <param name="SourceId">The video the clip is cut from (<see cref="SourceMedia.Id"/>); its times are seconds on that video.</param>
public sealed record ExportClip(int ClipId, int Number, string Label, double Start, double End, LosslessCut? Lossless,
    int SourceId = SourceMedia.FirstId)
{
    /// <summary>Where the output of this clip starts in the source (earlier than <see cref="Start"/> for lossless cuts).</summary>
    public double OutputStart => Lossless?.EffectiveStart ?? Start;

    public double OutputDuration => End - OutputStart;
}

public enum ExportStepKind
{
    /// <summary>Stream-copy one clip.</summary>
    Cut,

    /// <summary>Join the cut clips with the concat demuxer.</summary>
    Concat,

    /// <summary>Re-encode one clip.</summary>
    Encode,

    /// <summary>Re-encode all clips into one file in a single pass.</summary>
    EncodeMerged,
}

/// <param name="Weight">Share of the whole export's progress, 0..1.</param>
public sealed record ExportStep(ExportStepKind Kind, string Name, string OutputPath, bool IsTemporary, double Duration,
    IReadOnlyList<ExportClip> Clips, double Weight);

/// <summary>Everything an export will do, decided up front so it can be shown and tested.</summary>
/// <param name="Replacements">
/// Outputs that replace an existing file (<see cref="ExportSettings.Overwrite"/>): each is written under a temporary
/// name (the key) and moved over the old file (the value) once complete, so a failed export leaves the old file.
/// </param>
/// <param name="Source">The video the clips are cut from; the first of them when they come from several.</param>
/// <param name="Videos">When the clips come from several videos: each one, by video id. Null otherwise.</param>
/// <param name="Layout">When the clips come from several videos: how they become one output. Null otherwise.</param>
public sealed record ExportPlan(
    MediaInfo Source,
    ExportSettings Settings,
    IReadOnlyList<ExportClip> Clips,
    IReadOnlyList<ExportStep> Steps,
    IReadOnlyList<string> Outputs,
    string? ConcatListPath,
    string? ChaptersPath,
    IReadOnlyDictionary<string, string>? Replacements = null,
    IReadOnlyDictionary<int, MediaInfo>? Videos = null,
    ExportLayout? Layout = null)
{
    /// <summary>The file a clip is cut from.</summary>
    public MediaInfo InfoOf(ExportClip clip) => Videos?.GetValueOrDefault(clip.SourceId) ?? Source;

    /// <summary>Temporary files the export creates and removes (a replacement is gone once it has been moved).</summary>
    public IEnumerable<string> TemporaryFiles =>
        Steps.Where(s => s.IsTemporary).Select(s => s.OutputPath)
            .Concat(new[] { ConcatListPath, ChaptersPath }.OfType<string>())
            .Concat(Replacements?.Keys ?? []);

    public double OutputDuration => Clips.Sum(c => c.OutputDuration);
}

/// <summary>Turns a project and export settings into an <see cref="ExportPlan"/>.</summary>
public static partial class ExportPlanner
{
    /// <summary>Share of progress given to the final concat step of a lossless merge.</summary>
    public const double ConcatWeight = 0.08;

    /// <summary>Plans the export of a project with one video, cut from <paramref name="source"/>.</summary>
    /// <exception cref="InvalidOperationException">Nothing to export, or the settings are not supported.</exception>
    /// <param name="fileExists">Checks whether an output name is taken (tests pass a fake).</param>
    /// <param name="tempId">Distinguishes this export's temporary files; random by default.</param>
    public static ExportPlan Plan(Project project, MediaInfo source, IReadOnlyList<double> keyframes, ExportSettings settings,
        Func<string, bool>? fileExists = null, string? tempId = null) =>
        Plan(project, new Dictionary<int, ExportSource> { [project.Source?.Id ?? SourceMedia.FirstId] = new(source, keyframes) },
            settings, fileExists, tempId);

    /// <summary>
    /// Plans the export: each clip is cut from its own video (<paramref name="sources"/>, by video id, needed for every
    /// video an included clip comes from). Clips from several videos merged into one file are joined as
    /// <see cref="ExportLayout"/> says; lossless only when the videos can be joined without re-encoding.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Nothing to export, the settings are not supported, or the videos cannot be joined losslessly (the message says why).
    /// </exception>
    public static ExportPlan Plan(Project project, IReadOnlyDictionary<int, ExportSource> sources, ExportSettings settings,
        Func<string, bool>? fileExists = null, string? tempId = null)
    {
        fileExists ??= File.Exists;
        if (settings.Mode == CutMode.SmartCut)
            throw new InvalidOperationException("Smart cut is not available yet.");
        // The included clips, less any seconds an earlier clip already exports (only projects saved with overlapping clips).
        var parts = project.OutputParts();
        if (parts.Count == 0)
            throw new InvalidOperationException("There are no clips to export. Include at least one clip.");

        // The videos the clips come from, in timeline order, and the first of them, which a merged file takes after.
        var used = Used(project, parts, sources);
        var first = used[0].Source;
        var source = first.Info;
        bool lossless = settings.Mode == CutMode.Lossless;
        var layout = used.Count > 1 && settings.Merge
            ? ExportLayout.Create([.. used.Select(u => (u.Id, u.Source.Info))], settings)
            : null;
        if (layout is not null && lossless
            && ExportLayout.LosslessProblem([.. used.Select(u => (u.Id, u.Source.Info))], settings) is { } problem)
            throw new InvalidOperationException(problem + " " + LosslessAdvice);

        if (lossless && settings.Merge)
            parts = JoinTouching(parts);
        var bySource = used.ToDictionary(u => u.Id, u => u.Source);
        var clips = parts.Select((p, i) =>
        {
            var from = bySource[p.Clip.SourceId];
            return new ExportClip(p.Clip.Id, i + 1, p.Clip.Label, p.Start, p.End,
                lossless
                    ? CutPlanner.Plan(p.Start, p.End, from.Keyframes, from.Info.Family, from.Info.Video?.HasBFrames ?? false,
                        from.Info.Video?.FrameDuration ?? 1 / 30.0)
                    : null,
                p.Clip.SourceId);
        }).ToList();

        string folder = settings.OutputFolder;
        string ext = settings.Extension;
        var names = OutputNames(project, settings);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        tempId ??= Guid.NewGuid().ToString("N")[..12];
        string Temp(string suffix) => Path.Combine(folder, $".highlightcut-tmp-{tempId}{suffix}");

        // Where output i goes, and the file ffmpeg writes for it: a new name gets " (2)" when it is taken, unless the
        // old file is to be replaced; two outputs with one name (a pattern without {n}) are always told apart.
        (string Final, string Target) Output(int i)
        {
            if (!settings.Overwrite)
            {
                string unique = UniquePath(names[i], p => fileExists(p) || reserved.Contains(p), reserved);
                return (unique, unique);
            }
            string final = UniquePath(names[i], reserved.Contains, reserved);
            if (!fileExists(final))
                return (final, final);
            string target = Temp($"-new{i + 1:000}.{ext}");
            replacements[target] = final;
            return (final, target);
        }

        var steps = new List<ExportStep>();
        var outputs = new List<string>();
        string? listPath = null, chaptersPath = null;
        double total = clips.Sum(c => c.OutputDuration);

        if (settings.Merge)
        {
            var (final, target) = Output(0);
            outputs.Add(final);
            if (settings.AddChapters)
                chaptersPath = Temp(".ffmeta");
            if (lossless)
            {
                foreach (var c in clips)
                {
                    steps.Add(new ExportStep(ExportStepKind.Cut, $"{c.Number} · {c.Label}", Temp($"-{c.Number:000}.{ext}"), true,
                        c.OutputDuration, [c], (1 - ConcatWeight) * Share(c.OutputDuration, total, clips.Count)));
                }
                listPath = Temp(".ffconcat");
                steps.Add(new ExportStep(ExportStepKind.Concat, $"Merge into {Path.GetFileName(final)}", target, false, total, clips,
                    ConcatWeight));
            }
            else
            {
                steps.Add(new ExportStep(ExportStepKind.EncodeMerged, $"Merge into {Path.GetFileName(final)}", target, false, total, clips, 1));
            }
        }
        else
        {
            foreach (var c in clips)
            {
                var (final, target) = Output(c.Number - 1);
                outputs.Add(final);
                steps.Add(new ExportStep(lossless ? ExportStepKind.Cut : ExportStepKind.Encode, Path.GetFileName(final), target, false,
                    c.OutputDuration, [c], Share(c.OutputDuration, total, clips.Count)));
            }
        }

        if (outputs.Any(o => used.Any(u => string.Equals(Path.GetFullPath(o), Path.GetFullPath(u.Source.Info.Path), StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("The export would overwrite the source file.");

        return new ExportPlan(source, settings, clips, steps, outputs, listPath, chaptersPath, replacements.Count > 0 ? replacements : null,
            used.Count > 1 ? used.ToDictionary(u => u.Id, u => u.Source.Info) : null, layout);
    }

    /// <summary>What to do instead when <see cref="LosslessMergeProblem"/> says lossless cannot join the videos.</summary>
    public const string LosslessAdvice = "Re-encode to join them, or export separate files.";

    /// <summary>
    /// Why a lossless export of <paramref name="project"/> into one file cannot join its videos (see
    /// <see cref="ExportLayout.LosslessProblem"/>), or null when it can: one video, or videos alike enough. Videos not in
    /// <paramref name="sources"/> are not judged. The Export dialog says this before anything is exported.
    /// </summary>
    public static string? LosslessMergeProblem(Project project, IReadOnlyDictionary<int, MediaInfo> sources, ExportSettings settings)
    {
        var used = project.Sources
            .Where(s => sources.ContainsKey(s.Id) && project.OutputParts().Any(p => p.Clip.SourceId == s.Id))
            .Select(s => (s.Id, sources[s.Id]))
            .ToList();
        return ExportLayout.LosslessProblem(used, settings);
    }

    /// <summary>The videos <paramref name="parts"/> come from, in timeline order.</summary>
    private static List<(int Id, ExportSource Source)> Used(Project project, IReadOnlyList<OutputPart> parts,
        IReadOnlyDictionary<int, ExportSource> sources)
    {
        var ids = parts.Select(p => p.Clip.SourceId).Distinct()
            .OrderBy(id => project.SourceIndexOf(id) is var i and >= 0 ? i : int.MaxValue)
            .ToList();
        return [.. ids.Select(id => (id, sources.TryGetValue(id, out var s) ? s
            : throw new InvalidOperationException($"{project.FindSource(id)?.FileName ?? $"Video {id}"} is not open, so it cannot be exported.")))];
    }

    /// <summary>
    /// The files an export writes before " (2)" is added to a taken name: <see cref="ExportSettings.FileNamePattern"/> for
    /// the merged file, or for each included clip (each <see cref="Project.OutputParts"/>).
    /// </summary>
    public static IReadOnlyList<string> OutputNames(Project project, ExportSettings settings)
    {
        string ext = "." + settings.Extension;
        string Name(int number, string label, bool merged) => Path.Combine(settings.OutputFolder,
            ExportFileNames.Fill(settings.FileNamePattern, settings.BaseName, number, label, settings.Date, merged) + ext);
        return settings.Merge
            ? [Name(1, "", merged: true)]
            : [.. project.OutputParts().Select((p, i) => Name(i + 1, p.Clip.Label, merged: false))];
    }

    /// <summary>
    /// Lower-case file-name friendly form of a clip label, e.g. "Demo — import" → "demo-import".
    /// Letters of any script are kept ("Вступ" → "вступ").
    /// </summary>
    public static string Slug(string text)
    {
        string slug = NonAlphanumeric().Replace(text.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "clip" : slug;
    }

    /// <summary>Adds " (2)", " (3)", … before the extension until the path is free.</summary>
    internal static string UniquePath(string path, Func<string, bool> taken, HashSet<string> reserved)
    {
        string candidate = path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int n = 2; taken(candidate); n++)
            candidate = Path.Combine(dir, $"{name} ({n}){ext}");
        reserved.Add(candidate);
        return candidate;
    }

    /// <summary>
    /// Parts that follow on in the source as well as in the output, as one stretch (only within one video: clips meeting at the
    /// join between two videos are two cuts). A stream-copy cut starts at the
    /// keyframe before its in-point, so cutting touching clips separately would play the frames between that keyframe
    /// and the join twice. The stretch keeps the first clip's id and label (one chapter).
    /// </summary>
    internal static IReadOnlyList<OutputPart> JoinTouching(IReadOnlyList<OutputPart> parts)
    {
        var joined = new List<OutputPart>();
        foreach (var part in parts)
        {
            if (joined.Count > 0 && part.Clip.SourceId == joined[^1].Clip.SourceId
                && Math.Abs(part.Start - joined[^1].End) <= Project.OverlapTolerance)
                joined[^1] = joined[^1] with { End = part.End };
            else
                joined.Add(part);
        }
        return joined;
    }

    private static double Share(double duration, double total, int count) => total > 0 ? duration / total : 1.0 / count;

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonAlphanumeric();
}
