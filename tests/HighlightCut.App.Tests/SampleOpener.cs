using HighlightCut.App.Demo;
using HighlightCut.App.Services;

namespace HighlightCut.App.Tests;

/// <summary>Opens every path as the design's sample video, so UI tests need no real media.</summary>
internal sealed class SampleOpener : IMediaOpener
{
    public List<string> Opened { get; } = [];

    public Task<OpenedMedia> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        Opened.Add(path);
        if (path.Contains("missing", StringComparison.Ordinal))
            throw new FileNotFoundException("The file does not exist.", path);
        return Task.FromResult(new OpenedMedia(DesignSample.Source with { Path = path }, new DesignSample(), DesignSample.SourceInfo));
    }
}
