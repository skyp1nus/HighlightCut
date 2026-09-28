using System.Text.RegularExpressions;
using OurCut.Core.Model;
using OurCut.Media.Probing;

namespace OurCut.Media.Export;

/// <summary>One included clip as it will be exported.</summary>
/// <param name="Number">1-based position among the exported clips.</param>
/// <param name="Lossless">Cut points for stream copy; null when re-encoding.</param>
public sealed record ExportClip(int ClipId, int Number, string Label, double Start, double End, LosslessCut? Lossless)
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
public sealed record ExportPlan(
    MediaInfo Source,
    ExportSettings Settings,
    IReadOnlyList<ExportClip> Clips,
    IReadOnlyList<ExportStep> Steps,
    IReadOnlyList<string> Outputs,
    string? ConcatListPath,
    string? ChaptersPath,
    IReadOnlyDictionary<string, string>? Replacements = null)
{
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

    /// <exception cref="InvalidOperationException">Nothing to export, or the settings are not supported.</exception>
    /// <param name="fileExists">Checks whether an output name is taken (tests pass a fake).</param>
    /// <param name="tempId">Distinguishes this export's temporary files; random by default.</param>
    public static ExportPlan Plan(Project project, MediaInfo source, IReadOnlyList<double> keyframes, ExportSettings settings,
        Func<string, bool>? fileExists = null, string? tempId = null)
    {
        fileExists ??= File.Exists;
        if (settings.Mode == CutMode.SmartCut)
            throw new InvalidOperationException("Smart cut is not available yet.");
        // The included clips, less any seconds an earlier clip already exports (only projects saved with overlapping clips).
        var parts = project.OutputParts();
        if (parts.Count == 0)
            throw new InvalidOperationException("There are no clips to export. Include at least one clip.");

        bool lossless = settings.Mode == CutMode.Lossless;
        if (lossless && settings.Merge)
            parts = JoinTouching(parts);
        var clips = parts.Select((p, i) => new ExportClip(p.Clip.Id, i + 1, p.Clip.Label, p.Start, p.End,
            lossless
                ? CutPlanner.Plan(p.Start, p.End, keyframes, source.Family, source.Video?.HasBFrames ?? false,
                    source.Video?.FrameDuration ?? 1 / 30.0)
                : null)).ToList();

        string folder = settings.OutputFolder;
        string ext = settings.Extension;
        var names = OutputNames(project, settings);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        tempId ??= Guid.NewGuid().ToString("N")[..12];
        string Temp(string suffix) => Path.Combine(folder, $".ourcut-tmp-{tempId}{suffix}");

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

        if (outputs.Any(o => string.Equals(Path.GetFullPath(o), Path.GetFullPath(source.Path), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The export would overwrite the source file.");

        return new ExportPlan(source, settings, clips, steps, outputs, listPath, chaptersPath, replacements.Count > 0 ? replacements : null);
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
    /// Parts that follow on in the source as well as in the output, as one stretch. A stream-copy cut starts at the
    /// keyframe before its in-point, so cutting touching clips separately would play the frames between that keyframe
    /// and the join twice. The stretch keeps the first clip's id and label (one chapter).
    /// </summary>
    internal static IReadOnlyList<OutputPart> JoinTouching(IReadOnlyList<OutputPart> parts)
    {
        var joined = new List<OutputPart>();
        foreach (var part in parts)
        {
            if (joined.Count > 0 && Math.Abs(part.Start - joined[^1].End) <= Project.OverlapTolerance)
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
