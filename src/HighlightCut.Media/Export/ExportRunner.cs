using System.Text;
using FFMpegCore;
using FFMpegCore.Exceptions;
using HighlightCut.Media.Probing;
using HighlightCut.Media.Tools;

namespace HighlightCut.Media.Export;

/// <param name="StepIndex">Index of the running step in <see cref="ExportPlan.Steps"/>.</param>
/// <param name="StepFraction">Progress of that step, 0..1.</param>
/// <param name="Overall">Progress of the whole export, 0..1.</param>
public readonly record struct ExportProgress(int StepIndex, double StepFraction, double Overall);

/// <summary>Runs an <see cref="ExportPlan"/> with ffmpeg (through FFMpegCore).</summary>
public static class ExportRunner
{
    /// <summary>ffmpeg reads its list and metadata files as UTF-8 and does not skip a BOM.</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Runs every step in order. Temporary files are always removed; on failure or cancellation the
    /// output being written is removed too, so no half-written file is left behind. A file being replaced
    /// (<see cref="ExportPlan.Replacements"/>) is only touched once its new version is complete.
    /// </summary>
    /// <returns>The files written.</returns>
    /// <exception cref="MediaToolException">ffmpeg failed; the message has its last error line.</exception>
    /// <exception cref="OperationCanceledException">The export was cancelled.</exception>
    public static async Task<IReadOnlyList<string>> RunAsync(ExportPlan plan, IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string ffmpeg = NativeTools.FindTool("ffmpeg")
            ?? throw new MediaToolException("ffmpeg was not found. Run scripts/fetch-deps.ps1 or install FFmpeg.");
        var options = new FFOptions { BinaryFolder = Path.GetDirectoryName(ffmpeg)!, UseCache = true };

        Directory.CreateDirectory(plan.Settings.OutputFolder);
        var written = new List<string>();
        string? current = null;
        double done = 0;
        var active = plan;

        // One step with ffmpeg; its error lines become the exception's message.
        async Task RunFfmpegAsync(ExportPlan stepPlan, ExportStep step, int index, double before)
        {
            var errors = new Queue<string>();
            var command = FfmpegCommands.ForStep(stepPlan, step)
                .NotifyOnProgress(t =>
                {
                    double f = step.Duration > 0 ? Math.Clamp(t.TotalSeconds / step.Duration, 0, 1) : 0;
                    progress?.Report(new ExportProgress(index, f, before + f * step.Weight));
                })
                .NotifyOnError(line =>
                {
                    lock (errors)
                    {
                        errors.Enqueue(line);
                        if (errors.Count > 20)
                            errors.Dequeue();
                    }
                })
                .CancellableThrough(cancellationToken);
            try
            {
                await command.ProcessAsynchronously(throwOnError: true, options).ConfigureAwait(false);
            }
            catch (Exception e) when (e is FFMpegException or Instances.Exceptions.InstanceFileNotFoundException
                                      && !cancellationToken.IsCancellationRequested)
            {
                string tail;
                lock (errors)
                    tail = string.Join('\n', errors.Where(l => !l.StartsWith("frame=", StringComparison.Ordinal)
                                                             && !l.StartsWith("size=", StringComparison.Ordinal)));
                throw new MediaToolException("ffmpeg", -1, tail.Length > 0 ? tail : e.Message);
            }
        }

        // Everything that writes a file, temporary ones included, is inside the try: whenever the export stops,
        // the finally removes the temporary files from the output folder.
        try
        {
            if (plan.ConcatListPath is not null)
            {
                var parts = plan.Steps.Where(s => s.Kind == ExportStepKind.Cut && s.IsTemporary).Select(s => s.OutputPath);
                await File.WriteAllTextAsync(plan.ConcatListPath, FfmpegFiles.ConcatList(parts), Utf8, CancellationToken.None).ConfigureAwait(false);
            }
            // A re-encoded merge is frame-accurate, so the planned durations are exact. Lossless cuts come out
            // a little longer than planned (ffmpeg ends a stream copy by decode time), so their chapters are
            // written from the cut files just before they are joined.
            if (plan.ChaptersPath is not null && plan.ConcatListPath is null)
                await WriteChaptersAsync(plan, plan.Clips.Select(c => c.OutputDuration).ToList()).ConfigureAwait(false);

            for (int i = 0; i < plan.Steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = plan.Steps[i];
                if (step.Kind == ExportStepKind.Concat && plan.ChaptersPath is not null)
                    await WriteChaptersAsync(plan, await CutDurationsAsync(plan, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                current = step.OutputPath;
                int index = i;
                double before = done;
                progress?.Report(new ExportProgress(i, 0, before));

                try
                {
                    await RunFfmpegAsync(active, step, index, before).ConfigureAwait(false);
                }
                catch (MediaToolException) when (active.Settings.GpuEncoder is not null && !cancellationToken.IsCancellationRequested)
                {
                    // The GPU encoder could not take this video (its size, say): the CPU encodes this step and the rest.
                    await TryDeleteAsync(step.OutputPath).ConfigureAwait(false);
                    active = active with { Settings = active.Settings with { GpuEncoder = null } };
                    progress?.Report(new ExportProgress(i, 0, before));
                    await RunFfmpegAsync(active, step, index, before).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(step.OutputPath))
                    throw new MediaToolException($"ffmpeg did not write {Path.GetFileName(step.OutputPath)}.");

                done += step.Weight;
                if (!step.IsTemporary)
                {
                    string output = step.OutputPath;
                    if (plan.Replacements is { } replacements && replacements.TryGetValue(output, out string? old))
                    {
                        File.Move(output, old, overwrite: true);
                        output = old;
                    }
                    written.Add(output);
                }
                current = null;
                progress?.Report(new ExportProgress(i, 1, Math.Min(1, done)));
            }
            return written;
        }
        catch
        {
            if (current is not null)
                await TryDeleteAsync(current).ConfigureAwait(false);
            if (plan.Settings.Merge)
            {
                // Not a file that was to be replaced: it is still the old one.
                foreach (string o in plan.Outputs.Except(plan.Replacements?.Values ?? [], StringComparer.OrdinalIgnoreCase))
                    await TryDeleteAsync(o).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            foreach (string temp in plan.TemporaryFiles)
                await TryDeleteAsync(temp).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the chapters file whole (it is small); cancellation is checked between steps instead.</summary>
    private static Task WriteChaptersAsync(ExportPlan plan, IReadOnlyList<double> durations) =>
        File.WriteAllTextAsync(plan.ChaptersPath!, FfmpegFiles.Chapters(plan.Clips.Select((c, i) => (c.Label, durations[i]))),
            Utf8, CancellationToken.None);

    /// <summary>Real durations of the temporary cut files, which the concat demuxer uses as offsets.</summary>
    private static async Task<IReadOnlyList<double>> CutDurationsAsync(ExportPlan plan, CancellationToken cancellationToken)
    {
        var cuts = plan.Steps.Where(s => s.Kind == ExportStepKind.Cut && s.IsTemporary).ToList();
        var durations = new List<double>(cuts.Count);
        foreach (var cut in cuts)
        {
            double? real = null;
            try
            {
                real = await MediaProbe.ProbeDurationAsync(cut.OutputPath, cancellationToken).ConfigureAwait(false);
            }
            catch (MediaToolException)
            {
                // Fall back to the planned duration; chapters are then only slightly off.
            }
            durations.Add(real ?? cut.Duration);
        }
        return durations;
    }

    /// <summary>Deletes a file, retrying briefly while a just-killed ffmpeg or a virus scanner still holds it.</summary>
    private static async Task TryDeleteAsync(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5)
                    return; // Left for the user to remove.
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
    }
}
