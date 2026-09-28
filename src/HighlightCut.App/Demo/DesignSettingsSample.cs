using HighlightCut.App.ViewModels;
using HighlightCut.Transcription.Models;

namespace HighlightCut.App.Demo;

/// <summary>
/// The prototype's Settings → Transcription and MCP server samples (<c>MODELS</c>, the disk, <c>MCPT</c>) in
/// design/project/HighlightCut.dc.html. Sizes follow the design, not the catalog; whisper-medium exists only here.
/// </summary>
internal static class DesignSettingsSample
{
    public static IReadOnlyList<(TranscriptionModel Model, ModelState State, double Progress, string? Error)> Models { get; } =
    [
        (ModelCatalog.Parakeet with { DownloadSize = 1_300_000_000 }, ModelState.Installed, 1, null),
        (ModelCatalog.Find("whisper-large-v3-turbo")! with { DownloadSize = 1_600_000_000 }, ModelState.Installed, 1, null),
        (ModelCatalog.Find("whisper-small")! with { Id = "whisper-medium", DownloadSize = 1_500_000_000 }, ModelState.NotInstalled, 0, null),
        (ModelCatalog.Find("whisper-small")! with { DownloadSize = 488_000_000 }, ModelState.Failed, 212.0 / 488, "Connection lost at 212 of 488 MB"),
        (ModelCatalog.Find("whisper-base.en")! with { DownloadSize = 148_000_000 }, ModelState.Downloading, 0.64, null),
    ];

    public static DiskSpace Disk { get; } = new(1_400_000_000, "D:");

    public const string ClientName = "Claude Desktop";
    public static TimeSpan ConnectedFor { get; } = TimeSpan.FromMinutes(12);
    public const string OtherWindowProject = "podcast_ep12.highlightcut";
    public const string McpCommand = @"C:\Program Files\HighlightCut\HighlightCut.exe";
    public const string ConfigPath = @"%APPDATA%\Claude\claude_desktop_config.json";
}
