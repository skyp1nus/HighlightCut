using CommunityToolkit.Mvvm.ComponentModel;
using HighlightCut.Transcription.Models;

namespace HighlightCut.App.ViewModels;

/// <summary>The line under a model's id: why its download failed, why it does not fit, or a recommendation.</summary>
public sealed partial class TranscriptionModelViewModel
{
    /// <summary>"Needs 1.5 GB · 1.4 GB free on D:" while the model does not fit; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note), nameof(HasNote))]
    public partial string? SpaceNote { get; set; }

    public bool IsFailed => State == ModelState.Failed;
    public bool IsNoSpace => State == ModelState.NoSpace;

    public string? Recommendation => Model.Id == ModelCatalog.Parakeet.Id ? "Multilingual, includes Ukrainian" : null;

    public string? Note => State switch
    {
        ModelState.Failed => Error,
        ModelState.NoSpace => SpaceNote,
        _ => Recommendation,
    };

    public bool HasNote => !string.IsNullOrEmpty(Note);
}
