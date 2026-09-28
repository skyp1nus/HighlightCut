using HighlightCut.App.Services;

namespace HighlightCut.App.ViewModels;

// The General, Playback and Export sections' part of loading and opening the dialog.
public sealed partial class SettingsViewModel
{
    partial void InitGeneralPlaybackExport()
    {
        InitPlayback();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsOpen) && IsOpen)
            {
                RefreshGeneral();
                RefreshNamePreview();
            }
        };
    }

    partial void LoadGeneralPlaybackExport(AppSettings settings)
    {
        LoadGeneral(settings.General);
        LoadPlayback(settings.Playback);
        LoadExportDefaults(settings.Export);
        // Keep what is shown (unknown values read as defaults); a section missing from the file stays missing.
        _settings = _settings with
        {
            General = Shown(settings.General, ToGeneralSettings(), new GeneralSettings()),
            Playback = Shown(settings.Playback, ToPlaybackSettings(), new PlaybackSettings()),
            Export = Shown(settings.Export, ToExportDefaults(), new ExportDefaults()),
        };
        RefreshNamePreview();
    }

    private static T? Shown<T>(T? loaded, T shown, T defaults) where T : class =>
        loaded is null && shown.Equals(defaults) ? null : shown;

    /// <summary>Shows the design's sample General, Playback and Export settings (demo mode).</summary>
    public void LoadGeneralPlaybackExportDemo()
    {
        bool wasLoading = _loading;
        _loading = true;
        LoadGeneral(null);
        IsRecentListCleared = false;
        CacheFolder = @"C:\Users\mara\AppData\Local\HighlightCut\cache";
        CacheUsedText = "1.8 GB · 42 videos";
        CanClearCache = true;
        AppVersion = "0.9.0";
        FfmpegVersion = "7.1";
        LibmpvVersion = "0.39.0";
        _copiedTimer?.Stop();
        _copiedTimer = null;
        DiagnosticsCopied = false;

        AudioDevice = SystemDefaultDevice;
        AudioDevices = [SystemDefaultDevice, "Speakers (Realtek(R) Audio)", "Headphones (WH-1000XM4)", "DELL U2723QE (NVIDIA High Definition Audio)"];
        LoadPlayback(null);

        LoadExportDefaults(null);
        // Shown only: the Export dialog's demo screens keep the plain defaults.
        FixedExportFolder = @"D:\Videos\Exports";
        GpuEncoderNote = "Detected: NVIDIA NVENC (RTX 4070)";
        _loading = wasLoading;
        RefreshNamePreview();
    }
}
