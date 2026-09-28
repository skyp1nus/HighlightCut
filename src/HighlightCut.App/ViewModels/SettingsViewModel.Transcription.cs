using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HighlightCut.App.Demo;
using HighlightCut.App.Services;
using HighlightCut.Transcription.Models;
using Fillers = HighlightCut.Core.Transcripts.FillerWords;

namespace HighlightCut.App.ViewModels;

/// <summary>Free space on the drive that holds the models folder; <see cref="Drive"/> is "D:" on Windows, null elsewhere.</summary>
public readonly record struct DiskSpace(long Free, string? Drive);

/// <summary>Settings → Transcription: transcribe on open, filler words, and which models fit on the disk.</summary>
public sealed partial class SettingsViewModel
{
    // The design edits these two; the saved dictionary could hold more.
    private static readonly (string Code, string Label)[] FillerLanguageNames = [("en", "English"), ("uk", "Ukrainian")];

    private DiskSpace? _space;

    /// <summary>Transcribe a video as soon as it is opened; when off, only when someone asks.</summary>
    [ObservableProperty]
    public partial bool TranscribeOnOpen { get; set; }

    /// <summary>One row of chips per language, then the "Add…" box.</summary>
    public IReadOnlyList<FillerLanguageViewModel> FillerLanguages { get; private set; } = [];

    /// <summary>Reads the free space of the models folder's drive; null when it cannot be read.</summary>
    internal Func<string, DiskSpace?> ProbeDisk { get; set; } = ProbeDiskSpace;

    public bool HasNoSpaceModels => Models.Any(m => m.IsNoSpace);

    /// <summary>The line under the models table when a model does not fit.</summary>
    public string NoSpaceText
    {
        get
        {
            var tooBig = Models.Where(m => m.IsNoSpace).ToList();
            if (tooBig.Count == 0 || _space is not { } space)
                return "";
            string drive = space.Drive ?? "the disk";
            return tooBig.Count == 1
                ? $"{tooBig[0].Id} needs {tooBig[0].Size} and {drive} has {FormatFree(space.Free)} free. Free up space or choose another models folder."
                : $"{tooBig.Count.ToString(CultureInfo.InvariantCulture)} models need more than the {FormatFree(space.Free)} free on {drive}. Free up space or choose another models folder.";
        }
    }

    /// <summary>"212 GB", "1.4 GB" or "850 MB".</summary>
    public static string FormatFree(long bytes) => bytes >= 10_000_000_000
        ? (bytes / 1e9).ToString("0", CultureInfo.InvariantCulture) + " GB"
        : TranscriptionModel.FormatSize(bytes);

    private static DiskSpace? ProbeDiskSpace(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root))
                return null;
            string? drive = OperatingSystem.IsWindows() && root.Length >= 2 && root[1] == ':' ? root[..2] : null;
            return new DiskSpace(new DriveInfo(root).AvailableFreeSpace, drive);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads the free space again and marks the models that are not installed and do not fit.</summary>
    internal void RefreshSpace()
    {
        _space = _editor.IsDemo ? DesignSettingsSample.Disk : ProbeDisk(ModelsFolder);
        foreach (var m in Models.Where(m => m.State is ModelState.NotInstalled or ModelState.NoSpace))
        {
            bool tooBig = _space is { } s && s.Free < m.Model.DownloadSize;
            m.State = tooBig ? ModelState.NoSpace : ModelState.NotInstalled;
            m.SpaceNote = tooBig && _space is { } space
                ? $"Needs {m.Size} · {FormatFree(space.Free)} free" + (space.Drive is { } d ? " on " + d : "")
                : null;
        }
        OnPropertyChanged(nameof(DiskFreeText));
        OnPropertyChanged(nameof(HasNoSpaceModels));
        OnPropertyChanged(nameof(NoSpaceText));
    }

    /// <summary>Puts the catalog's models back after the demo's table (demo, then a real file opened).</summary>
    private void RestoreCatalogModels()
    {
        foreach (var m in Models.Where(m => ModelCatalog.Find(m.Id) is null).ToList())
            Models.Remove(m);
        foreach (var m in Models)
            m.Model = ModelCatalog.Find(m.Id)!;
    }

    partial void InitTranscriptionMcp()
    {
        FillerLanguages = [.. FillerLanguageNames.Select(l => new FillerLanguageViewModel(this, l.Code, l.Label, FillerWordsOf(l.Code)))];
        InitMcp();
    }

    partial void LoadTranscriptionMcp(AppSettings settings)
    {
        TranscribeOnOpen = settings.Transcription.TranscribeOnOpen;
        LoadMcp(settings.Mcp);
    }

    partial void OnTranscribeOnOpenChanged(bool value)
    {
        if (!_loading)
            UpdateSettings(s => s with { Transcription = s.Transcription with { TranscribeOnOpen = value } });
    }

    partial void OnFillerWordsChanged(IReadOnlyDictionary<string, IReadOnlyList<string>> value)
    {
        foreach (var language in FillerLanguages)
            language.Show(FillerWordsOf(language.Code));
    }

    private IReadOnlyList<string> FillerWordsOf(string code) => FillerWords.TryGetValue(code, out var words) ? words : [];

    /// <summary>The design's model table (prototype <c>MODELS</c>): every state once, on a nearly full drive.</summary>
    private void LoadDesignTranscription()
    {
        TranscribeOnOpen = true;
        for (int i = 0; i < DesignSettingsSample.Models.Count; i++)
        {
            var (model, state, progress, error) = DesignSettingsSample.Models[i];
            var row = Models.FirstOrDefault(m => m.Id == model.Id);
            if (row is null)
            {
                row = new TranscriptionModelViewModel(this, model);
                Models.Insert(Math.Min(i, Models.Count), row);
            }
            row.Model = model;
            row.State = state;
            row.Progress = progress;
            row.Error = error;
        }
        RefreshSpace();
    }

    /// <summary>The design's "no model" screen: nothing installed or downloading.</summary>
    internal void LoadDesignNoModels()
    {
        foreach (var m in Models)
        {
            m.State = ModelState.NotInstalled;
            m.Progress = 0;
            m.Error = null;
        }
        RefreshModelOptions(Model?.Value);
    }
}
