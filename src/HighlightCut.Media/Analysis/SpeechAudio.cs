using System.Globalization;
using HighlightCut.Media.Probing;

namespace HighlightCut.Media.Analysis;

/// <summary>The audio speech recognition listens to: every audio track mixed to 16 kHz mono.</summary>
public static class SpeechAudio
{
    public const int SampleRate = 16000;

    /// <summary>
    /// ffmpeg arguments writing raw 32-bit float samples to stdout. The audio is timed from the start of the file
    /// (silence is added if a track starts late), as keyframe times are, so word times match the timeline.
    /// </summary>
    /// <param name="streams">Container stream indexes to mix; every audio stream if null.</param>
    public static IReadOnlyList<string> Arguments(MediaInfo info, IReadOnlyList<int>? streams = null)
    {
        var indexes = streams ?? [.. info.Audio.Select(a => a.Index)];
        if (indexes.Count == 0)
            throw new InvalidOperationException("The file has no audio.");
        string resample = string.Create(CultureInfo.InvariantCulture,
            $"aresample={SampleRate}:async=1:first_pts=0,aformat=sample_fmts=flt:channel_layouts=mono");
        string graph = indexes.Count == 1
            ? $"[0:{indexes[0].ToString(CultureInfo.InvariantCulture)}]{resample}[out]"
            : string.Join(';', indexes.Select((index, i) => string.Create(CultureInfo.InvariantCulture, $"[0:{index}]{resample}[m{i}]")))
              + ";" + string.Concat(indexes.Select((_, i) => $"[m{i}]"))
              + string.Create(CultureInfo.InvariantCulture, $"amix=inputs={indexes.Count}:normalize=0:duration=longest[out]");
        return ["-v", "error", "-i", info.Path, "-vn", "-sn", "-dn", "-filter_complex", graph, "-map", "[out]",
            "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"];
    }
}
