namespace HighlightCut.App.Services;

public enum HardwareDecodingMode
{
    Auto,
    Off,
}

public enum VideoRendererMode
{
    Auto,
    Software,
}

/// <summary>Settings → Playback.</summary>
/// <param name="AudioDevice">mpv audio device name; null is the system default.</param>
/// <param name="Volume">Last volume and speed, kept only when <paramref name="RememberVolumeAndSpeed"/> is on.</param>
public sealed record PlaybackSettings(
    HardwareDecodingMode HardwareDecoding = HardwareDecodingMode.Auto,
    VideoRendererMode Renderer = VideoRendererMode.Auto,
    string? AudioDevice = null,
    double JumpSeconds = 1,
    bool RememberVolumeAndSpeed = true,
    double? Volume = null,
    double? Speed = null)
{
    /// <summary>mpv's hwdec value for this choice (a method, so it is not written into settings.json).</summary>
    public string MpvHardwareDecoding() => MpvHardwareDecoding(HardwareDecoding);

    /// <summary>mpv's hwdec value: copy-back decoding, which works with every renderer, or none.</summary>
    public static string MpvHardwareDecoding(HardwareDecodingMode mode) => mode == HardwareDecodingMode.Off ? "no" : "auto-copy";
}
