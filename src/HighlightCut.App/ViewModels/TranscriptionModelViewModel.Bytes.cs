using CommunityToolkit.Mvvm.ComponentModel;
using HighlightCut.Transcription.Models;

namespace HighlightCut.App.ViewModels;

/// <summary>How far a running download is, in bytes (the Transcript tab's "212 of 488 MB").</summary>
public sealed partial class TranscriptionModelViewModel
{
    /// <summary>Bytes downloaded so far.</summary>
    [ObservableProperty]
    public partial long ReceivedBytes { get; private set; }

    /// <summary>The download's size once the server has said; null before.</summary>
    [ObservableProperty]
    public partial long? TotalBytes { get; private set; }

    /// <summary>The download is complete and its archive is being unpacked.</summary>
    [ObservableProperty]
    public partial bool IsUnpacking { get; private set; }

    /// <summary>Where the running download is (ModelInstaller's <see cref="InstallProgress"/>).</summary>
    internal void ReportBytes(InstallPhase phase, long received, long? total)
    {
        IsUnpacking = phase == InstallPhase.Unpacking;
        ReceivedBytes = received;
        TotalBytes = total;
    }
}
