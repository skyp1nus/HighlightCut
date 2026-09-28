using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HighlightCut.App.Services;
using HighlightCut.Media.Export;
using HighlightCut.Media.Probing;

namespace HighlightCut.App.ViewModels;

// Settings → Export: what the dialog starts with.
public sealed partial class ExportViewModel
{
    private (string? Path, ExportDefaults Defaults)? _defaultsFor;

    /// <summary>Settings → Export; applied for each new file and whenever they change while the dialog is closed.</summary>
    [ObservableProperty]
    public partial ExportDefaults Defaults { get; set; } = new();

    partial void OnDefaultsChanged(ExportDefaults value)
    {
        if (Stage == ExportStage.Closed)
            ApplyDefaults(Preview is { } p && !_editor.IsDemo ? p.Info : null);
    }

    /// <summary>Starts from Settings → Export. Without a file (the demo sample) the folder and audio names stay as they are.</summary>
    private void ApplyDefaults(MediaInfo? info)
    {
        var d = Defaults;
        if (_defaultsFor == (info?.Path, d))
            return;
        _defaultsFor = (info?.Path, d);
        Mode = d.Mode == ExportDefaultMode.Reencode ? ExportMode.Encode : ExportMode.Copy;
        Merge = d.Merge;
        AddChapters = d.Chapters;
        KeepAllTracks = d.AudioTracks == ExportAudioTracksMode.KeepAll;
        Video = d.Video switch
        {
            ReencodeVideoPreset.H264Fast => VideoEncoding.H264Fast,
            ReencodeVideoPreset.H265 => VideoEncoding.H265,
            _ => VideoEncoding.H264Quality,
        };
        Container = d.ContainerFor(info?.NaturalExtension);
        if (info is not null)
        {
            OutputFolder = d.FolderFor(info.Path) ?? OutputFolder;
            var first = info.Audio.FirstOrDefault();
            string copy = first is null ? "Copy"
                : $"Copy ({first.Codec.ToUpperInvariant()} {(first.SampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} kHz)";
            AudioChoices = [new(copy, AudioEncoding.Copy), new(AudioEncoding.Aac192.Label, AudioEncoding.Aac192)];
        }
        Audio = d.Audio == ReencodeAudioChoice.Aac192 ? AudioChoices[^1] : AudioChoices[0];
    }
}
