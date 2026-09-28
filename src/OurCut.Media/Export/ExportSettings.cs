namespace OurCut.Media.Export;

public enum CutMode
{
    /// <summary>Stream copy: no re-encoding, the in-point moves back to a keyframe.</summary>
    Lossless,

    /// <summary>Frame-accurate cuts, re-encoding only around cut points. Not implemented yet.</summary>
    SmartCut,

    /// <summary>Full re-encode with the chosen codecs.</summary>
    Reencode,
}

public enum OutputContainer
{
    Mp4,
    Mov,
    Mkv,
}

/// <summary>Video settings for <see cref="CutMode.Reencode"/>.</summary>
public sealed record VideoEncoding(string Codec, int Crf, string Preset, string Label)
{
    public static VideoEncoding H264Quality { get; } = new("libx264", 18, "medium", "H.264 · CRF 18 · medium");
    public static VideoEncoding H264Fast { get; } = new("libx264", 23, "veryfast", "H.264 · CRF 23 · fast");
    public static VideoEncoding H265 { get; } = new("libx265", 22, "medium", "H.265 · CRF 22 · medium");

    public static IReadOnlyList<VideoEncoding> All { get; } = [H264Quality, H264Fast, H265];
}

/// <summary>Audio settings for <see cref="CutMode.Reencode"/>.</summary>
/// <param name="Codec">"copy" keeps the source audio (not possible when merging, which must re-encode).</param>
public sealed record AudioEncoding(string Codec, int BitrateKbps, string Label)
{
    public static AudioEncoding Copy { get; } = new("copy", 0, "Copy");
    public static AudioEncoding Aac192 { get; } = new("aac", 192, "AAC 192 kb/s");

    public static IReadOnlyList<AudioEncoding> All { get; } = [Copy, Aac192];

    public bool IsCopy => Codec == "copy";
}

public sealed record ExportSettings
{
    public CutMode Mode { get; init; } = CutMode.Lossless;
    public OutputContainer Container { get; init; } = OutputContainer.Mp4;

    /// <summary>One file with all included clips, or one file per clip.</summary>
    public bool Merge { get; init; } = true;

    /// <summary>Chapter per clip, named after the clip (merged output only).</summary>
    public bool AddChapters { get; init; } = true;

    /// <summary>
    /// Keep every audio and subtitle stream. When false, only <see cref="AudioStreamIndexes"/> are kept
    /// and subtitles are dropped.
    /// </summary>
    public bool KeepAllTracks { get; init; } = true;

    /// <summary>Container stream indexes of the audio to keep when <see cref="KeepAllTracks"/> is false.</summary>
    public IReadOnlyList<int> AudioStreamIndexes { get; init; } = [];

    /// <summary>
    /// Volume in dB of the audio streams that do not play at 0 dB, by container stream index. Such a stream is
    /// re-encoded (AAC when it would otherwise be copied); every other stream is left as the mode says.
    /// </summary>
    public IReadOnlyDictionary<int, double> AudioGainsDb { get; init; } = new Dictionary<int, double>();

    public required string OutputFolder { get; init; }

    /// <summary>Base of the output file names, usually the project name: the pattern's {project}.</summary>
    public required string BaseName { get; init; }

    /// <summary>How outputs are named (<see cref="ExportFileNames"/>): "{project}-cut-{n}" by default.</summary>
    public string FileNamePattern { get; init; } = ExportFileNames.DefaultPattern;

    /// <summary>The pattern's {date}.</summary>
    public DateOnly Date { get; init; } = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>
    /// Replace a file that has an output's name (once the new one is complete); otherwise " (2)" is added to the name.
    /// </summary>
    public bool Overwrite { get; init; }

    public VideoEncoding Video { get; init; } = VideoEncoding.H264Quality;

    /// <summary>Re-encodes <see cref="Video"/> on this GPU encoder instead of the CPU; the CPU takes over if it fails.</summary>
    public GpuEncoder? GpuEncoder { get; init; }
    public AudioEncoding Audio { get; init; } = AudioEncoding.Copy;

    public string Extension => Container switch
    {
        OutputContainer.Mov => "mov",
        OutputContainer.Mkv => "mkv",
        _ => "mp4",
    };

    /// <summary>ffmpeg muxer name for <c>-f</c>.</summary>
    public string Muxer => Container switch
    {
        OutputContainer.Mov => "mov",
        OutputContainer.Mkv => "matroska",
        _ => "mp4",
    };

    public bool IsMovLike => Container is OutputContainer.Mp4 or OutputContainer.Mov;
}
