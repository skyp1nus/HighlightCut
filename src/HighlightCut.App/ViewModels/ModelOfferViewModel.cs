using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.Transcription.Models;

namespace HighlightCut.App.ViewModels;

/// <summary>
/// The offer to download the suggested transcription model (Parakeet), in the Transcript tab and the welcome tour's
/// Transcript step: the button, the note under it and the progress while it downloads. The download is Settings →
/// Transcription's own, so it goes on when the tab or the tour is closed.
/// </summary>
public sealed partial class ModelOfferViewModel : ViewModelBase
{
    private readonly EditorViewModel _editor;
    private readonly List<(DateTime At, long Bytes)> _byteSamples = [];

    public ModelOfferViewModel(EditorViewModel editor)
    {
        _editor = editor;
        Model = editor.Settings.Models.FirstOrDefault(m => m.Id == ModelCatalog.Parakeet.Id);
        foreach (var model in editor.Settings.Models)
            model.PropertyChanged += OnModelPropertyChanged;
        editor.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.Model) or nameof(SettingsViewModel.Engine))
                RaiseReady();
        };
    }

    /// <summary>The model offered (Parakeet).</summary>
    public TranscriptionModelViewModel? Model { get; }

    public bool ShowDownloadButton => Model is { IsNotInstalled: true } or { IsFailed: true };
    public bool IsDownloading => Model is { IsDownloading: true };

    /// <summary>"Download parakeet-tdt-0.6b-v3 (487 MB)", or "Retry download (487 MB)" after a failed one.</summary>
    public string DownloadButtonText => Model switch
    {
        { IsFailed: true } m => $"Retry download ({m.Size})",
        { } m => $"Download {m.Id} ({m.Size})",
        _ => "",
    };

    /// <summary>Under the button: the recommendation, why the download failed, or why it does not fit.</summary>
    public string? Note => IsDownloading ? null : Model?.Note;
    public bool ShowNote => !string.IsNullOrEmpty(Note);
    public bool ShowOffer => ShowDownloadButton || ShowNote;

    /// <summary>"312 of 487 MB · 48 MB/s", or "Unpacking…".</summary>
    public string DownloadDetail
    {
        get
        {
            if (Model is not { } m)
                return "";
            if (m.IsUnpacking)
                return "Unpacking…";
            long total = m.TotalBytes ?? m.Model.DownloadSize;
            double received = _editor.IsDemo ? m.Progress * total : m.ReceivedBytes;
            string text = total >= 1_000_000_000
                ? string.Create(CultureInfo.InvariantCulture, $"{received / 1e9:0.0} of {total / 1e9:0.0} GB")
                : string.Create(CultureInfo.InvariantCulture, $"{received / 1e6:0} of {total / 1e6:0} MB");
            double? speed = _editor.IsDemo ? 48e6 : Speed();
            return speed is { } s ? text + string.Create(CultureInfo.InvariantCulture, $" · {s / 1e6:0} MB/s") : text;
        }
    }

    /// <summary>
    /// The installed model transcription uses, or null while there is none. (The demo transcribes without one, so there
    /// it is the first installed model.)
    /// </summary>
    public string? ReadyModelId => _editor.IsDemo
        ? _editor.Settings.Models.FirstOrDefault(m => m.IsInstalled)?.Id
        : _editor.Settings.ActiveModel?.Id;

    public bool IsReady => ReadyModelId is not null;

    /// <summary>Downloads the model; nothing is transcribed because of it (the Transcript tab adds that for its video).</summary>
    [RelayCommand]
    public void Download() => Model?.DownloadCommand.Execute(null);

    [RelayCommand]
    private void Cancel() => Model?.CancelDownloadCommand.Execute(null);

    /// <summary>Bytes per second over the last second or more of the download; null until known.</summary>
    private double? Speed()
    {
        if (_byteSamples.Count < 2)
            return null;
        var (at, bytes) = _byteSamples[^1];
        for (int i = _byteSamples.Count - 2; i >= 0; i--)
        {
            double seconds = (at - _byteSamples[i].At).TotalSeconds;
            if (seconds >= 1)
                return (bytes - _byteSamples[i].Bytes) / seconds;
        }
        return null;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, Model))
        {
            if (e.PropertyName == nameof(TranscriptionModelViewModel.ReceivedBytes))
                SampleBytes(Model!.ReceivedBytes);
            foreach (string name in (string[])[nameof(ShowDownloadButton), nameof(IsDownloading), nameof(DownloadButtonText),
                         nameof(DownloadDetail), nameof(Note), nameof(ShowNote), nameof(ShowOffer)])
                OnPropertyChanged(name);
        }
        if (e.PropertyName == nameof(TranscriptionModelViewModel.State))
            RaiseReady();
    }

    private void RaiseReady()
    {
        OnPropertyChanged(nameof(ReadyModelId));
        OnPropertyChanged(nameof(IsReady));
    }

    private void SampleBytes(long bytes)
    {
        var now = DateTime.UtcNow;
        if (_byteSamples.Count > 0 && bytes < _byteSamples[^1].Bytes)
            _byteSamples.Clear();
        _byteSamples.Add((now, bytes));
        // Keep a few seconds: enough for a one-second window.
        _byteSamples.RemoveAll(s => (now - s.At).TotalSeconds > 3);
    }
}
