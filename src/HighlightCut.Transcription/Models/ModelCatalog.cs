using System.Globalization;

namespace HighlightCut.Transcription.Models;

public enum TranscriptionEngine
{
    /// <summary>NVIDIA Parakeet TDT through sherpa-onnx (ONNX Runtime).</summary>
    Parakeet,

    /// <summary>OpenAI Whisper through sherpa-onnx (int8 ONNX encoder and decoder).</summary>
    Whisper,
}

/// <summary>How a model is published.</summary>
public enum ModelPackage
{
    /// <summary>One file, downloaded as is.</summary>
    SingleFile,

    /// <summary>
    /// A .tar.bz2 archive with one top folder (sherpa-onnx releases), unpacked without that folder; only the model's
    /// files (and its test_wavs samples) are kept.
    /// </summary>
    TarBz2,
}

/// <summary>A speech-to-text model HighlightCut can download.</summary>
/// <param name="Id">Folder name under the models folder, e.g. "whisper-small".</param>
/// <param name="Languages">What it understands, for the model table.</param>
/// <param name="DownloadSize">Approximate download size in bytes (the server's size is used once known).</param>
/// <param name="Files">Files that must be there once installed.</param>
public sealed record TranscriptionModel(
    string Id,
    TranscriptionEngine Engine,
    string Languages,
    long DownloadSize,
    Uri Url,
    ModelPackage Package,
    IReadOnlyList<string> Files)
{
    /// <summary>"487 MB", "1.6 GB".</summary>
    public string SizeText => FormatSize(DownloadSize);

    public static string FormatSize(long bytes) =>
        bytes >= 1_000_000_000
            ? (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : Math.Max(1, Math.Round(bytes / 1e6)).ToString("0", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>
/// The models listed in Settings → Transcription, best first. All run on sherpa-onnx and come from its GitHub
/// releases (int8 builds: several times smaller and faster on the CPU than the full-precision ones).
/// </summary>
public static class ModelCatalog
{
    private const string Releases = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/";

    public static TranscriptionModel Parakeet { get; } = new(
        "parakeet-tdt-0.6b-v3", TranscriptionEngine.Parakeet, "25 European languages", 487_170_055,
        new Uri(Releases + "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2"),
        ModelPackage.TarBz2, ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"]);

    public static IReadOnlyList<TranscriptionModel> All { get; } =
    [
        Parakeet,
        Whisper("whisper-large-v3-turbo", "turbo", 563_790_207, "99 languages"),
        Whisper("whisper-small", "small", 639_387_718, "99 languages"),
        Whisper("whisper-base.en", "base.en", 208_576_005, "English"),
    ];

    public static TranscriptionModel? Find(string id) => All.FirstOrDefault(m => m.Id == id);

    /// <param name="name">The sherpa-onnx name: archive sherpa-onnx-whisper-&lt;name&gt;, files &lt;name&gt;-encoder.int8.onnx etc.</param>
    private static TranscriptionModel Whisper(string id, string name, long size, string languages) =>
        new(id, TranscriptionEngine.Whisper, languages, size, new Uri($"{Releases}sherpa-onnx-whisper-{name}.tar.bz2"), ModelPackage.TarBz2,
            [$"{name}-encoder.int8.onnx", $"{name}-decoder.int8.onnx", $"{name}-tokens.txt"]);

    /// <summary>The language a model is limited to ("en" for English-only models), or null.</summary>
    public static string? OnlyLanguage(TranscriptionModel model) => model.Id.EndsWith(".en", StringComparison.Ordinal) ? "en" : null;
}
