using System.Globalization;
using System.Text;
using FFMpegCore;
using HighlightCut.Media.Ffmpeg;
using HighlightCut.Media.Probing;

namespace HighlightCut.Media.Export;

/// <summary>
/// Builds the ffmpeg command for each export step with FFMpegCore. The builders are pure, so tests
/// can check <see cref="FFMpegArgumentProcessor.Arguments"/> without running ffmpeg.
/// </summary>
public static class FfmpegCommands
{
    /// <summary>
    /// The command for one step. Each clip is cut from its own video with that video's audio choices
    /// (<see cref="ExportSettings.ForSource"/>); a file joining several videos follows <see cref="ExportPlan.Layout"/>.
    /// </summary>
    public static FFMpegArgumentProcessor ForStep(ExportPlan plan, ExportStep step)
    {
        var clip = step.Clips[0];
        var info = plan.InfoOf(clip);
        var settings = plan.Settings.ForSource(clip.SourceId);
        return step.Kind switch
        {
            ExportStepKind.Cut when plan.Layout is { } layout && step.IsTemporary =>
                JoinedCut(info, clip, plan.Settings, layout, step.OutputPath),
            ExportStepKind.Cut => LosslessCut(info, clip, settings, step.OutputPath, final: !step.IsTemporary),
            ExportStepKind.Concat => Concat(plan.ConcatListPath!, plan.ChaptersPath, plan.Settings, step.OutputPath,
                plan.Layout?.SharedGains ?? AudioGains(info, settings)),
            ExportStepKind.Encode => EncodeClip(info, clip, settings, step.OutputPath),
            ExportStepKind.EncodeMerged when plan.Layout is { } layout =>
                EncodeJoined(plan, layout, step.Clips, plan.Settings, plan.ChaptersPath, step.OutputPath),
            ExportStepKind.EncodeMerged => EncodeMerged(info, step.Clips, settings, plan.ChaptersPath, step.OutputPath),
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    /// <summary>Stream copy of one clip: <c>-ss</c> before <c>-i</c> (fast, starts at a keyframe), <c>-t</c> after.</summary>
    /// <param name="final">
    /// The clip's own output. Only that one changes audio volume: the pieces of a merge are copied as they are, and
    /// the volume is applied once when they are joined, so the re-encoded audio has no gaps at the joins.
    /// </param>
    public static FFMpegArgumentProcessor LosslessCut(MediaInfo source, ExportClip clip, ExportSettings settings, string output, bool final) =>
        CopyCut(source, clip, settings, output, final, StreamMaps(source, settings), final ? AudioGains(source, settings) : []);

    /// <summary>
    /// Stream copy of one piece of a lossless file that joins several videos: the picture and the video's track for each
    /// output track (<see cref="ExportLayout.Lanes"/>), so every piece has the same streams. A track whose volume differs
    /// between the videos gets this video's volume here (re-encoded); the others are copied and changed once when joined.
    /// </summary>
    public static FFMpegArgumentProcessor JoinedCut(MediaInfo source, ExportClip clip, ExportSettings settings, ExportLayout layout, string output)
    {
        var inputs = layout.Inputs[clip.SourceId];
        var maps = new List<string>();
        if (source.Video is { } v)
            maps.Add("-map 0:" + v.Index.ToString(CultureInfo.InvariantCulture));
        maps.AddRange(inputs.Select(a => "-map 0:" + (a.Stream ?? throw new ArgumentException("A video lacks a track.", nameof(layout)))
            .Index.ToString(CultureInfo.InvariantCulture)));
        var gains = Enumerable.Range(0, inputs.Count).Where(k => !layout.IsShared(k)).Select(k => (k, inputs[k].GainDb)).ToList();
        return CopyCut(source, clip, settings, output, final: false, maps, gains);
    }

    private static FFMpegArgumentProcessor CopyCut(MediaInfo source, ExportClip clip, ExportSettings settings, string output, bool final,
        IEnumerable<string> maps, IReadOnlyList<(int Position, double GainDb)> gains)
    {
        var cut = clip.Lossless ?? throw new ArgumentException("The clip has no lossless cut points.", nameof(clip));
        return FFMpegArguments
            .FromFileInput(source.Path, verifyExists: false, o => o.WithCustomArgument("-ss " + FfmpegText.Seconds(cut.SeekTo)))
            .OutputToFile(output, overwrite: true, o =>
            {
                o.WithCustomArgument("-t " + FfmpegText.Seconds(cut.Duration));
                foreach (string map in maps)
                    o.WithCustomArgument(map);
                o.WithCustomArgument("-c copy");
                foreach (string gain in GainArguments(gains, reencode: true))
                    o.WithCustomArgument(gain);
                // The kept lead-in before -ss gets negative timestamps; shift them to start at zero.
                if (cut.SeekTo > 0)
                    o.WithCustomArgument("-avoid_negative_ts make_zero");
                o.WithCustomArgument("-map_metadata 0 -ignore_unknown");
                if (final && settings.IsMovLike)
                    o.WithCustomArgument("-movflags +faststart");
                o.ForceFormat(settings.Muxer);
            })
            .WithLogLevel(FFMpegCore.Enums.FFMpegLogLevel.Error);
    }

    /// <summary>Joins cut clips losslessly with the concat demuxer, optionally adding chapters.</summary>
    /// <param name="audioGains">Audio streams to change the volume of (<see cref="AudioGains"/>); only these are re-encoded.</param>
    public static FFMpegArgumentProcessor Concat(string listPath, string? chaptersPath, ExportSettings settings, string output,
        IReadOnlyList<(int Position, double GainDb)>? audioGains = null)
    {
        var args = FFMpegArguments.FromFileInput(listPath, verifyExists: false, o => o.WithCustomArgument("-f concat -safe 0"));
        if (chaptersPath is not null)
            args = args.AddFileInput(chaptersPath, verifyExists: false, o => o.WithCustomArgument("-f ffmetadata"));
        return args
            .OutputToFile(output, overwrite: true, o =>
            {
                o.WithCustomArgument("-map 0 -c copy -map_metadata 0");
                foreach (string gain in GainArguments(audioGains ?? [], reencode: true))
                    o.WithCustomArgument(gain);
                if (chaptersPath is not null)
                    o.WithCustomArgument("-map_chapters 1");
                if (settings.IsMovLike)
                    o.WithCustomArgument("-movflags +faststart");
                o.ForceFormat(settings.Muxer);
            })
            .WithLogLevel(FFMpegCore.Enums.FFMpegLogLevel.Error);
    }

    /// <summary>Re-encodes one clip. Input seeking is frame-accurate when transcoding.</summary>
    public static FFMpegArgumentProcessor EncodeClip(MediaInfo source, ExportClip clip, ExportSettings settings, string output) =>
        FFMpegArguments
            .FromFileInput(source.Path, verifyExists: false, o => o.WithCustomArgument("-ss " + FfmpegText.Seconds(clip.Start)))
            .OutputToFile(output, overwrite: true, o =>
            {
                o.WithCustomArgument("-t " + FfmpegText.Seconds(clip.End - clip.Start));
                foreach (string map in StreamMaps(source, settings))
                    o.WithCustomArgument(map);
                foreach (string codec in Codecs(source, settings, merged: false))
                    o.WithCustomArgument(codec);
                foreach (string gain in GainArguments(AudioGains(source, settings), reencode: settings.Audio.IsCopy))
                    o.WithCustomArgument(gain);
                // Copied streams (audio set to "copy", subtitles) would otherwise start at the keyframe
                // before the in-point, leaving a lead-in that Matroska keeps.
                o.WithCustomArgument("-copypriorss 0");
                o.WithCustomArgument("-map_metadata 0");
                if (settings.IsMovLike)
                    o.WithCustomArgument("-movflags +faststart");
                o.ForceFormat(settings.Muxer);
            })
            .WithLogLevel(FFMpegCore.Enums.FFMpegLogLevel.Error);

    /// <summary>
    /// Re-encodes all clips into one file in one pass: each clip is its own seeked input and the
    /// concat filter joins them (no gaps from audio priming at the joins).
    /// </summary>
    public static FFMpegArgumentProcessor EncodeMerged(MediaInfo source, IReadOnlyList<ExportClip> clips, ExportSettings settings,
        string? chaptersPath, string output)
    {
        if (clips.Count == 0)
            throw new ArgumentException("Nothing to encode.", nameof(clips));
        FFMpegArguments? args = null;
        foreach (var c in clips)
        {
            string seek = $"-ss {FfmpegText.Seconds(c.Start)} -t {FfmpegText.Seconds(c.End - c.Start)}";
            args = args is null
                ? FFMpegArguments.FromFileInput(source.Path, verifyExists: false, o => o.WithCustomArgument(seek))
                : args.AddFileInput(source.Path, verifyExists: false, o => o.WithCustomArgument(seek));
        }
        if (chaptersPath is not null)
            args = args!.AddFileInput(chaptersPath, verifyExists: false, o => o.WithCustomArgument("-f ffmetadata"));

        var audio = SelectedAudio(source, settings);
        string graph = ConcatFilter(clips.Count, source.Video is not null, audio.Select(a => a.Position).ToList(),
            audio.Select(a => settings.AudioGainsDb.GetValueOrDefault(a.Index)).ToList());
        return args!
            .OutputToFile(output, overwrite: true, o =>
            {
                o.WithCustomArgument($"-filter_complex \"{graph}\"");
                if (source.Video is not null)
                    o.WithCustomArgument("-map \"[v]\"");
                for (int k = 0; k < audio.Count; k++)
                    o.WithCustomArgument($"-map \"[a{k}]\"");
                foreach (string codec in Codecs(source, settings, merged: true))
                    o.WithCustomArgument(codec);
                if (chaptersPath is not null)
                    o.WithCustomArgument("-map_chapters " + clips.Count.ToString(CultureInfo.InvariantCulture));
                o.WithCustomArgument("-map_metadata 0");
                if (settings.IsMovLike)
                    o.WithCustomArgument("-movflags +faststart");
                o.ForceFormat(settings.Muxer);
            })
            .WithLogLevel(FFMpegCore.Enums.FFMpegLogLevel.Error);
    }

    /// <summary>
    /// Re-encodes the clips of several videos into one file in one pass, like <see cref="EncodeMerged"/>. Every clip is its
    /// own seeked input from its video, brought to <paramref name="layout"/> before the concat filter joins them: the picture
    /// scaled to fit the first video's size without stretching (padded with black), at its frame rate (constant), and track N
    /// of each video at its volume, resampled to output track N (silence where a video has no such track).
    /// </summary>
    public static FFMpegArgumentProcessor EncodeJoined(ExportPlan plan, ExportLayout layout, IReadOnlyList<ExportClip> clips,
        ExportSettings settings, string? chaptersPath, string output)
    {
        if (clips.Count == 0)
            throw new ArgumentException("Nothing to encode.", nameof(clips));
        FFMpegArguments? args = null;
        foreach (var c in clips)
        {
            string path = plan.InfoOf(c).Path;
            string seek = $"-ss {FfmpegText.Seconds(c.Start)} -t {FfmpegText.Seconds(c.End - c.Start)}";
            args = args is null
                ? FFMpegArguments.FromFileInput(path, verifyExists: false, o => o.WithCustomArgument(seek))
                : args.AddFileInput(path, verifyExists: false, o => o.WithCustomArgument(seek));
        }
        if (chaptersPath is not null)
            args = args!.AddFileInput(chaptersPath, verifyExists: false, o => o.WithCustomArgument("-f ffmetadata"));

        string graph = JoinFilter(layout, [.. clips.Select(c => (plan.InfoOf(c), layout.Inputs[c.SourceId], c.End - c.Start))]);
        return args!
            .OutputToFile(output, overwrite: true, o =>
            {
                o.WithCustomArgument($"-filter_complex \"{graph}\"");
                if (layout.Video is not null)
                    o.WithCustomArgument("-map \"[v]\"");
                for (int k = 0; k < layout.Lanes.Count; k++)
                    o.WithCustomArgument($"-map \"[a{k}]\"");
                if (layout.Video is { } v)
                {
                    foreach (string codec in VideoCodecs(v, settings))
                        o.WithCustomArgument(codec);
                }
                if (layout.Lanes.Count > 0)
                    o.WithCustomArgument(AudioCodec(settings.Audio.IsCopy ? AudioEncoding.Aac192 : settings.Audio));
                if (chaptersPath is not null)
                    o.WithCustomArgument("-map_chapters " + clips.Count.ToString(CultureInfo.InvariantCulture));
                o.WithCustomArgument("-map_metadata 0");
                if (settings.IsMovLike)
                    o.WithCustomArgument("-movflags +faststart");
                o.ForceFormat(settings.Muxer);
            })
            .WithLogLevel(FFMpegCore.Enums.FFMpegLogLevel.Error);
    }

    /// <summary>
    /// The filter graph of <see cref="EncodeJoined"/>: each input brought to the layout, then the concat filter, e.g.
    /// <c>[0:v:0]scale=…,pad=…,setsar=1,fps=30,format=yuv420p[v0];[0:a:0]volume=-6dB,aresample=48000,aformat=…[a0x0];…</c>
    /// <c>[v0][a0x0][v1][a1x0]concat=n=2:v=1:a=1[v][a0]</c>.
    /// </summary>
    /// <param name="inputs">Per input (clip): its video, what it plays on each output track, and its length in seconds.</param>
    public static string JoinFilter(ExportLayout layout, IReadOnlyList<(MediaInfo Info, IReadOnlyList<LaneInput> Lanes, double Duration)> inputs)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        string w = layout.Width.ToString(ci), h = layout.Height.ToString(ci);
        for (int i = 0; i < inputs.Count; i++)
        {
            var (info, lanes, duration) = inputs[i];
            string d = FfmpegText.Seconds(duration);
            if (layout.Video is not null)
            {
                sb.Append(info.Video is not null
                    ? $"[{i}:v:0]scale={w}:{h}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,"
                    : $"color=c=black:s={w}x{h}:r={layout.FrameRate},trim=duration={d},");
                sb.Append(ci, $"fps={layout.FrameRate},format=yuv420p[v{i}];");
            }
            for (int k = 0; k < layout.Lanes.Count; k++)
            {
                var lane = layout.Lanes[k];
                string format = $"aresample={lane.SampleRate.ToString(ci)},aformat=sample_fmts=fltp:channel_layouts={lane.Layout}";
                sb.Append(lanes[k].Stream is { } stream
                    ? $"[{i}:a:{stream.Position.ToString(ci)}]" + (lanes[k].GainDb != 0 ? FfmpegText.VolumeFilter(lanes[k].GainDb) + "," : "") + format
                    : $"anullsrc=r={lane.SampleRate.ToString(ci)}:cl={lane.Layout},atrim=duration={d}");
                sb.Append(ci, $"[a{i}x{k}];");
            }
        }
        for (int i = 0; i < inputs.Count; i++)
        {
            if (layout.Video is not null)
                sb.Append(ci, $"[v{i}]");
            for (int k = 0; k < layout.Lanes.Count; k++)
                sb.Append(ci, $"[a{i}x{k}]");
        }
        sb.Append(ci, $"concat=n={inputs.Count}:v={(layout.Video is not null ? 1 : 0)}:a={layout.Lanes.Count}");
        if (layout.Video is not null)
            sb.Append("[v]");
        for (int k = 0; k < layout.Lanes.Count; k++)
            sb.Append(ci, $"[a{k}]");
        return sb.ToString();
    }

    /// <summary>
    /// <c>[0:v:0][0:a:0][1:v:0][1:a:0]concat=n=2:v=1:a=1[v][a0]</c> for the given audio positions. An audio stream with
    /// a gain leaves the concat as <c>[c0]</c> and goes through a volume filter: <c>;[c0]volume=-6dB[a0]</c>.
    /// </summary>
    /// <param name="gainsDb">Volume of each audio stream in dB, in the order of <paramref name="audioPositions"/>; 0 dB if null.</param>
    public static string ConcatFilter(int inputs, bool video, IReadOnlyList<int> audioPositions, IReadOnlyList<double>? gainsDb = null)
    {
        double Gain(int k) => gainsDb is not null && k < gainsDb.Count ? gainsDb[k] : 0;
        var sb = new StringBuilder();
        for (int i = 0; i < inputs; i++)
        {
            if (video)
                sb.Append(CultureInfo.InvariantCulture, $"[{i}:v:0]");
            foreach (int p in audioPositions)
                sb.Append(CultureInfo.InvariantCulture, $"[{i}:a:{p}]");
        }
        sb.Append(CultureInfo.InvariantCulture, $"concat=n={inputs}:v={(video ? 1 : 0)}:a={audioPositions.Count}");
        if (video)
            sb.Append("[v]");
        for (int k = 0; k < audioPositions.Count; k++)
            sb.Append(CultureInfo.InvariantCulture, $"[{(Gain(k) == 0 ? 'a' : 'c')}{k}]");
        for (int k = 0; k < audioPositions.Count; k++)
        {
            if (Gain(k) != 0)
                sb.Append(CultureInfo.InvariantCulture, $";[c{k}]{FfmpegText.VolumeFilter(Gain(k))}[a{k}]");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The kept audio streams whose volume changes: their position among the output's audio streams (the <c>K</c> of
    /// <c>-filter:a:K</c>) and their gain in dB.
    /// </summary>
    public static IReadOnlyList<(int Position, double GainDb)> AudioGains(MediaInfo source, ExportSettings settings) =>
        [.. SelectedAudio(source, settings)
            .Select((a, k) => (Position: k, GainDb: settings.AudioGainsDb.GetValueOrDefault(a.Index)))
            .Where(g => g.GainDb != 0)];

    /// <summary>
    /// <c>-filter:a:K volume=…</c> for each stream with a gain, plus <c>-c:a:K aac -b:a:K 192k</c> when the stream
    /// would otherwise be copied: filtering needs decoding, so only that stream is re-encoded and the rest still copied.
    /// </summary>
    private static IEnumerable<string> GainArguments(IReadOnlyList<(int Position, double GainDb)> gains, bool reencode)
    {
        var aac = AudioEncoding.Aac192;
        foreach (var (k, gain) in gains)
        {
            string p = k.ToString(CultureInfo.InvariantCulture);
            yield return $"-filter:a:{p} {FfmpegText.VolumeFilter(gain)}";
            if (reencode)
                yield return $"-c:a:{p} {aac.Codec} -b:a:{p} {aac.BitrateKbps.ToString(CultureInfo.InvariantCulture)}k";
        }
    }

    /// <summary><c>-map</c> options: the video stream, the kept audio streams and the subtitles the output can hold.</summary>
    public static IEnumerable<string> StreamMaps(MediaInfo source, ExportSettings settings)
    {
        if (source.Video is { } v)
            yield return "-map 0:" + v.Index.ToString(CultureInfo.InvariantCulture);
        foreach (var a in SelectedAudio(source, settings))
            yield return "-map 0:" + a.Index.ToString(CultureInfo.InvariantCulture);
        foreach (var s in KeptSubtitles(source, settings))
            yield return "-map 0:" + s.Index.ToString(CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<AudioStreamInfo> SelectedAudio(MediaInfo source, ExportSettings settings) =>
        settings.KeepAllTracks
            ? source.Audio
            : [.. source.Audio.Where(a => settings.AudioStreamIndexes.Contains(a.Index))];

    private static readonly string[] MatroskaSubtitles =
        ["subrip", "ass", "ssa", "webvtt", "dvd_subtitle", "hdmv_pgs_subtitle", "dvb_subtitle", "text"];

    /// <summary>
    /// Subtitle streams kept by stream copy: only with "keep all tracks", and only formats the output
    /// container can hold (MP4/MOV take <c>mov_text</c>, MKV the common text and bitmap formats).
    /// </summary>
    public static IReadOnlyList<SubtitleStreamInfo> KeptSubtitles(MediaInfo source, ExportSettings settings) =>
        !settings.KeepAllTracks
            ? []
            : [.. source.Subtitles.Where(s => settings.IsMovLike
                ? s.Codec == "mov_text"
                : MatroskaSubtitles.Contains(s.Codec))];

    /// <param name="merged">
    /// The concat filter decodes the audio, so it cannot be copied, and subtitles are not carried over.
    /// </param>
    private static IEnumerable<string> Codecs(MediaInfo source, ExportSettings settings, bool merged)
    {
        if (source.Video is { } v)
        {
            foreach (string codec in VideoCodecs(v, settings))
                yield return codec;
        }
        if (SelectedAudio(source, settings).Count > 0)
            yield return AudioCodec(settings.Audio.IsCopy && merged ? AudioEncoding.Aac192 : settings.Audio);
        if (!merged && KeptSubtitles(source, settings).Count > 0)
            yield return "-c:s copy";
    }

    /// <summary>The video encoder: on the GPU when the settings have one, else x264 or x265 in 8-bit 4:2:0.</summary>
    private static IEnumerable<string> VideoCodecs(VideoStreamInfo v, ExportSettings settings)
    {
        var enc = settings.Video;
        if (settings.GpuEncoder is { } gpu)
        {
            yield return gpu.Arguments(enc);
        }
        else
        {
            yield return $"-c:v {enc.Codec} -preset {enc.Preset} -crf {enc.Crf.ToString(CultureInfo.InvariantCulture)}";
            // Players expect 8-bit 4:2:0.
            if (!string.Equals(v.PixelFormat, "yuv420p", StringComparison.Ordinal))
                yield return "-pix_fmt yuv420p";
        }
        if (settings.IsMovLike && enc.Codec == "libx265")
            yield return "-tag:v hvc1";
    }

    private static string AudioCodec(AudioEncoding audio) => audio.IsCopy
        ? "-c:a copy"
        : $"-c:a {audio.Codec} -b:a {audio.BitrateKbps.ToString(CultureInfo.InvariantCulture)}k";
}

/// <summary>Text files the export writes for ffmpeg.</summary>
public static class FfmpegFiles
{
    /// <summary>
    /// A concat demuxer list. Paths are absolute with the <c>file:</c> protocol and single quotes
    /// escaped as <c>'\''</c>; the file must be written as UTF-8 without a BOM.
    /// </summary>
    public static string ConcatList(IEnumerable<string> files)
    {
        var sb = new StringBuilder("ffconcat version 1.0\n");
        foreach (string f in files)
            sb.Append("file 'file:").Append(Path.GetFullPath(f).Replace("'", @"'\''", StringComparison.Ordinal)).Append("'\n");
        return sb.ToString();
    }

    /// <summary>An FFMETADATA file with one chapter per clip, back to back.</summary>
    public static string Chapters(IEnumerable<(string Title, double Duration)> chapters)
    {
        var sb = new StringBuilder(";FFMETADATA1\n");
        long start = 0;
        foreach (var (title, duration) in chapters)
        {
            long end = start + (long)Math.Round(duration * 1000);
            sb.Append("\n[CHAPTER]\nTIMEBASE=1/1000\n")
              .Append(CultureInfo.InvariantCulture, $"START={start}\nEND={end}\n")
              .Append("title=").Append(EscapeMetadata(title)).Append('\n');
            start = end;
        }
        return sb.ToString();
    }

    /// <summary>FFMETADATA escaping: '=', ';', '#', '\' and newlines get a backslash.</summary>
    public static string EscapeMetadata(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (ch is '=' or ';' or '#' or '\\' or '\n')
                sb.Append('\\');
            sb.Append(ch == '\r' ? ' ' : ch);
        }
        return sb.ToString();
    }
}
