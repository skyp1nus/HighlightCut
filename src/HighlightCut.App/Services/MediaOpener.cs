using HighlightCut.Core.Model;
using HighlightCut.Media.Caching;
using HighlightCut.Media.Probing;

namespace HighlightCut.App.Services;

/// <summary>A probed media file, ready to edit.</summary>
/// <param name="Summary">Header text, e.g. "keynote.mp4 · 4K · 29.97 fps".</param>
public sealed record OpenedMedia(SourceMedia Source, IMediaPreview Preview, string Summary);

/// <summary>Opens media files for the editor. The real one uses ffprobe; tests use a fake.</summary>
public interface IMediaOpener
{
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="HighlightCut.Media.Tools.MediaToolException">ffprobe cannot read the file.</exception>
    Task<OpenedMedia> OpenAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Probes the file (off the UI thread) and opens its preview. Nothing else is read until the timeline's chips or a
/// feature ask for it, so the player can start at once.
/// </summary>
public sealed class FfmpegMediaOpener(MediaCache? cache) : IMediaOpener
{
    public async Task<OpenedMedia> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = await Task.Run(() => MediaProbe.ProbeAsync(Path.GetFullPath(path), cancellationToken), cancellationToken)
            .ConfigureAwait(true);
        var preview = new MediaPreview(info, cache);
        preview.Start();
        return new OpenedMedia(info.ToSourceMedia(), preview, info.Summary);
    }
}
