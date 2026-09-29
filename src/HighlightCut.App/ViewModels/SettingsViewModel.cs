using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HighlightCut.App.Services;
using HighlightCut.Transcription;
using HighlightCut.Transcription.Models;
using Fillers = HighlightCut.Core.Transcripts.FillerWords;

namespace HighlightCut.App.ViewModels;

public enum ModelState
{
    NotInstalled,
    Downloading,
    Installed,

    /// <summary>The last download failed; Retry resumes it.</summary>
    Failed,

    /// <summary>Not installed, and too big for the free space in the models folder.</summary>
    NoSpace,
}

/// <summary>A transcription model in the Settings → Transcription table.</summary>
public sealed partial class TranscriptionModelViewModel(SettingsViewModel owner, TranscriptionModel model) : ViewModelBase
{
    /// <summary>The catalog entry (the demo swaps in the design's sizes).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Id), nameof(Engine), nameof(Size), nameof(Languages), nameof(Recommendation), nameof(Note), nameof(HasNote))]
    public partial TranscriptionModel Model { get; internal set; } = model;

    public string Id => Model.Id;
    public string Engine => Model.Engine.ToString();
    public string Size => Model.SizeText;
    public string Languages => Model.Languages;

    /// <summary>Why the last download failed; null if it did not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note), nameof(HasNote))]
    public partial string? Error { get; set; }

    /// <summary>The running download, cancelled by the row's ✕.</summary>
    internal CancellationTokenSource? RunningDownload { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(IsDownloading), nameof(IsNotInstalled), nameof(IsFailed), nameof(IsNoSpace),
        nameof(Note), nameof(HasNote))]
    public partial ModelState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    public partial double Progress { get; set; }

    public bool IsInstalled => State == ModelState.Installed;
    public bool IsDownloading => State == ModelState.Downloading;
    public bool IsNotInstalled => State == ModelState.NotInstalled;
    public string PercentText => Math.Floor(Progress * 100).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>Downloads and deletes work (they are simulated in demo mode).</summary>
    public bool CanManage => owner.CanManageModels;

    [RelayCommand]
    private void Download() => owner.Download(this);

    [RelayCommand]
    private void Retry() => owner.Download(this);

    [RelayCommand]
    private void Delete() => owner.Delete(this);

    [RelayCommand]
    private void CancelDownload() => owner.Delete(this);
}

/// <summary>The settings dialog; each section lives in SettingsViewModel.&lt;Section&gt;.cs and saves as soon as it changes.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    public static IReadOnlyList<string> Sections { get; } = ["General", "Playback", "Export", "Transcription", "Keyboard", "MCP server"];
    public static IReadOnlyList<string> Engines { get; } = ["Auto", "Parakeet", "Whisper"];
    public static IReadOnlyList<string> Devices { get; } = ["Auto", "GPU", "CPU"];
    public static IReadOnlyList<string> Languages { get; } =
        ["Auto-detect", "English", "German", "Spanish", "French", "Japanese", "Portuguese", "Ukrainian"];

    private readonly EditorViewModel _editor;
    private DispatcherTimer? _downloadTimer;
    private bool _loading;
    private AppSettings _settings = AppSettings.Default;

    public SettingsViewModel(EditorViewModel editor)
    {
        _editor = editor;
        Models = [.. ModelCatalog.All.Select(m => new TranscriptionModelViewModel(this, m))];
        SectionOptions = [.. Sections.Select(name => new ChoiceOption(name, () => Section = name))];
        EngineOptions = [.. Engines.Select(e => new ChoiceOption(e, () => Engine = e))];
        DeviceOptions = [.. Devices.Select(d => new ChoiceOption(d, () => Device = d))];
        InitGeneralPlaybackExport();
        InitTranscriptionMcp();
        InitKeyboard();
        Load(AppSettings.Default);
        OnSectionChanged(Section);
        var (command, args) = EditorLauncher.McpCommand();
        McpCommand = command;
        McpArgs = args;
        // The first transcription with DirectML checks the GPU; the note then says what it found.
        GpuProbe.Checked += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(DeviceNote)));
    }

    /// <summary>Where settings are saved; null keeps them in memory (demo mode and tests).</summary>
    public AppSettingsStore? Store { get; set; }

    /// <summary>Claude's connection, for the MCP server section.</summary>
    public ClaudePanelViewModel Claude => _editor.Claude;

    /// <summary>Puts text on the clipboard; provided by the window.</summary>
    public Func<string, Task>? CopyText { get; set; }

    public IReadOnlyList<ChoiceOption> SectionOptions { get; }

    /// <summary>The section shown on the right.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneral), nameof(IsPlayback), nameof(IsExport), nameof(IsTranscription), nameof(IsKeyboard),
        nameof(IsMcp), nameof(SectionNote))]
    public partial string Section { get; set; } = "Transcription";

    public bool IsGeneral => Section == "General";
    public bool IsPlayback => Section == "Playback";
    public bool IsExport => Section == "Export";
    public bool IsTranscription => Section == "Transcription";
    public bool IsKeyboard => Section == "Keyboard";
    public bool IsMcp => Section == "MCP server";

    /// <summary>The header's line under the section name (prototype <c>SECSUB</c>).</summary>
    public string SectionNote => Section switch
    {
        "General" => "How HighlightCut starts, saves your work and stores its cache.",
        "Playback" => "Decoding, audio output and transport steps.",
        "Export" => "The defaults the Export dialog starts with. Each export can still change them.",
        "Transcription" => "Runs locally. Claude uses the transcript to find silences, quotes and filler words.",
        "Keyboard" => "Select a row, then Change to record a new shortcut.",
        "MCP server" => "Lets Claude Desktop and Claude Code open videos and edit the timeline in this window.",
        _ => "",
    };

    public ObservableCollection<TranscriptionModelViewModel> Models { get; }
    public IReadOnlyList<ChoiceOption> EngineOptions { get; }
    public IReadOnlyList<ChoiceOption> DeviceOptions { get; }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Engine { get; set; } = "Auto";

    [ObservableProperty]
    public partial string Device { get; set; } = "Auto";

    [ObservableProperty]
    public partial string Language { get; set; } = "Auto-detect";

    [ObservableProperty]
    public partial string ModelsFolder { get; set; } = "";

    /// <summary>Selected model id, or "best" for the best installed one (Auto engine only).</summary>
    [ObservableProperty]
    public partial ModelOption? Model { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ModelOption> ModelOptions { get; private set; } = [];

    public bool CanManageModels => _editor.IsDemo || Installer is not null;

    /// <summary>Downloads models; null in tests that do not download.</summary>
    public ModelInstaller? Installer { get; set; }

    private ModelStore InstalledModels => new(ModelsFolder);

    /// <summary>
    /// The model transcription uses: the chosen one, or with "Best available" the first installed one of the chosen
    /// engine (the table is ordered best first); null when none is installed (and always in demo mode).
    /// </summary>
    public TranscriptionModel? ActiveModel
    {
        get
        {
            if (_editor.IsDemo)
                return null;
            var installed = Models.Where(m => m.IsInstalled && (Engine == "Auto" || m.Engine == Engine)).ToList();
            return (Model?.Value is null or "best" ? installed.FirstOrDefault() : installed.FirstOrDefault(m => m.Id == Model.Value))?.Model;
        }
    }

    /// <summary>Where a model is installed.</summary>
    public string DirectoryOf(TranscriptionModel model) => InstalledModels.DirectoryOf(model);

    /// <summary>The chosen language as a two-letter code, or null for detection.</summary>
    public string? LanguageCode => Language switch
    {
        "English" => "en",
        "German" => "de",
        "Spanish" => "es",
        "French" => "fr",
        "Japanese" => "ja",
        "Portuguese" => "pt",
        "Ukrainian" => "uk",
        _ => null,
    };

    /// <summary>The model or language transcription should use changed, or a model finished installing.</summary>
    public event EventHandler? TranscriptionChanged;

    private void RaiseTranscriptionChanged()
    {
        if (!_loading)
            TranscriptionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The chosen device, for the recognizer.</summary>
    public TranscriptionDevice DeviceChoice => Device switch
    {
        "GPU" => TranscriptionDevice.Gpu,
        "CPU" => TranscriptionDevice.Cpu,
        _ => TranscriptionDevice.Auto,
    };

    /// <summary>The GPU runtime beside the app ("cuda", "directml"), or null; tests set it.</summary>
    public string? GpuProvider { get; set; } = RecognizerPlan.InstalledGpuProvider;

    /// <summary>The GPU DirectML would use ("AMD Radeon RX 7800 XT"), or null; tests set it.</summary>
    public string? GpuName { get; set; } = GpuAdapter.Current?.Name;

    /// <summary>What the DirectML check found for a model, or null before it has run; tests set it.</summary>
    public Func<TranscriptionModel?, GpuCheck?> GpuCheckFor { get; set; } = GpuProbe.Known;

    /// <summary>
    /// What transcription runs on with the chosen device: "CPU · 16 threads", "GPU (CUDA) · CPU if it fails",
    /// "GPU (DirectML) · AMD Radeon RX 7800 XT · 3.2× faster than the CPU".
    /// </summary>
    public string DeviceNote => _editor.IsDemo
        ? (Device == "CPU" ? "16 threads · ~4× slower" : "NVIDIA RTX 4070 · CUDA 12.4")
        : GpuProvider == "directml" ? DirectMLNote
        : (DeviceChoice, GpuProvider) switch
        {
            (TranscriptionDevice.Gpu, null) => "No GPU runtime is installed · choose Auto or CPU",
            (TranscriptionDevice.Auto, null) => RecognizerPlan.Cpu(Environment.ProcessorCount).Description + " · no GPU runtime is installed",
            (TranscriptionDevice.Auto, _) => RecognizerPlan.Choose(DeviceChoice, Environment.ProcessorCount, GpuProvider).Description + " · CPU if it fails",
            _ => RecognizerPlan.Choose(DeviceChoice, Environment.ProcessorCount, GpuProvider).Description,
        };

    /// <summary>Transcription runs on the GPU with the chosen device, as far as is known before it starts.</summary>
    public bool TranscribesOnGpu
    {
        get
        {
            try
            {
                var check = GpuProvider == "directml" ? GpuCheckFor(ActiveModel) : null;
                return RecognizerPlan.Choose(DeviceChoice, Environment.ProcessorCount, GpuProvider, check).OnGpu;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>With the DirectML runtime: the GPU, and what the check found for the active model.</summary>
    private string DirectMLNote
    {
        get
        {
            string cpu = RecognizerPlan.Cpu(Environment.ProcessorCount).Description;
            string gpu = "GPU (DirectML) · " + (GpuName ?? "no GPU found");
            var check = GpuCheckFor(ActiveModel);
            return (DeviceChoice, check) switch
            {
                (TranscriptionDevice.Cpu, _) => cpu,
                (TranscriptionDevice.Auto, null) => gpu + " · checked when transcription starts",
                (TranscriptionDevice.Auto, { Faster: true }) => $"{gpu} · {check.SpeedupText} faster than the CPU",
                (TranscriptionDevice.Auto, { Works: true }) => $"{cpu} · faster than DirectML on {GpuName}",
                (TranscriptionDevice.Auto, _) => $"{cpu} · DirectML: {check.Problem}",
                (_, null) => gpu,
                (_, { Works: true }) => $"{gpu} · " + (check.Faster ? $"{check.SpeedupText} faster than the CPU" : "slower than the CPU"),
                _ => $"DirectML: {check.Problem} · choose Auto or CPU",
            };
        }
    }

    /// <summary>The table header's "1.4 GB free on D:".</summary>
    public string DiskFreeText => _space is { } s ? FormatFree(s.Free) + " free" + (s.Drive is { } d ? " on " + d : "") : "";

    /// <summary>Everything the dialog saves: the loaded settings with every change since.</summary>
    public AppSettings Current => _settings with
    {
        Transcription = _settings.Transcription with
        {
            Engine = Engine,
            Model = Model?.Value ?? "best",
            Device = Device,
            Language = Language,
            ModelsFolder = ModelsFolder == AppSettingsStore.DefaultModelsFolder ? null : ModelsFolder,
            FillerWords = Fillers.AreDefaults(FillerWords) ? null : FillerWords,
        },
    };

    /// <summary>
    /// The one way a section changes settings: applies <paramref name="change"/> to <see cref="Current"/> and
    /// saves the result (not while loading).
    /// </summary>
    private void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        _settings = change(Current);
        if (!_loading)
            Store?.Save(_settings);
    }

    // Section hooks; Load runs while _loading, so nothing is saved back.
    partial void InitGeneralPlaybackExport();
    partial void InitTranscriptionMcp();
    partial void InitKeyboard();
    partial void LoadGeneralPlaybackExport(AppSettings settings);
    partial void LoadTranscriptionMcp(AppSettings settings);
    partial void LoadKeyboard(AppSettings settings);

    public void Load(AppSettings settings)
    {
        var fillers = FillerWords;
        _loading = true;
        _settings = settings;
        var t = settings.Transcription;
        Engine = Engines.Contains(t.Engine) ? t.Engine : "Auto";
        Device = Devices.Contains(t.Device) ? t.Device : "Auto";
        Language = Languages.Contains(t.Language) ? t.Language : "Auto-detect";
        // A folder the user chose stays; only one inside the old app folder follows it to its new place.
        ModelsFolder = string.IsNullOrWhiteSpace(t.ModelsFolder) ? AppSettingsStore.DefaultModelsFolder : AppDataFolder.Relocate(t.ModelsFolder);
        ScanModels(keepFailures: false);
        RefreshModelOptions(t.Model);
        SyncChoices();
        SetFillerWordLists(Fillers.WithDefaults(t.FillerWords));
        LoadGeneralPlaybackExport(settings);
        LoadTranscriptionMcp(settings);
        LoadKeyboard(settings);
        ApplyTimeline();
        _loading = false;
        if (!ReferenceEquals(fillers, FillerWords))
            FillerWordsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncChoices()
    {
        foreach (var o in EngineOptions)
            o.IsSelected = o.Label == Engine;
        foreach (var o in DeviceOptions)
            o.IsSelected = o.Label == Device;
    }

    /// <summary>Shows the design's sample settings (demo mode).</summary>
    public void LoadDemo()
    {
        var fillers = FillerWords;
        _loading = true;
        SetFillerWordLists(Fillers.Defaults);
        Engine = "Auto";
        Device = "Auto";
        Language = "Auto-detect";
        ModelsFolder = @"D:\HighlightCut\models";
        LoadDesignTranscription();
        RefreshModelOptions("best");
        SyncChoices();
        _loading = false;
        if (!ReferenceEquals(fillers, FillerWords))
            FillerWordsChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(DeviceNote));
        OnPropertyChanged(nameof(DiskFreeText));
        StartDownloadTimer();
    }

    public AppSettings ToSettings() => Current;

    // ---- Filler words (Settings → Transcription) -----------------------------------------------

    /// <summary>Filler words by language code ("en", "uk"): marked in the transcript.</summary>
    [ObservableProperty]
    public partial IReadOnlyDictionary<string, IReadOnlyList<string>> FillerWords { get; private set; } = Fillers.Defaults;

    /// <summary>Raised after <see cref="FillerWords"/> changed: edited, or read from the settings file.</summary>
    public event EventHandler? FillerWordsChanged;

    /// <summary>Replaces one language's filler words (trimmed, lower case, each once) and saves them.</summary>
    public void SetFillerWords(string language, IEnumerable<string> words)
    {
        var lists = new Dictionary<string, IReadOnlyList<string>>(FillerWords, StringComparer.Ordinal) { [language] = Fillers.Clean(words) };
        if (!SetFillerWordLists(lists))
            return;
        Save();
        if (!_loading)
            FillerWordsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool SetFillerWordLists(IReadOnlyDictionary<string, IReadOnlyList<string>> lists)
    {
        if (Fillers.Same(lists, FillerWords))
            return false;
        FillerWords = lists;
        return true;
    }

    partial void OnSectionChanged(string value)
    {
        foreach (var o in SectionOptions)
            o.IsSelected = o.Label == value;
    }

    [RelayCommand]
    public void Open()
    {
        ScanModels(keepFailures: true);
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    [RelayCommand]
    private async Task ChangeFolder()
    {
        if (_editor.Dialogs is null)
            return;
        string? folder = await _editor.Dialogs.PickFolderAsync("Transcription models folder", ModelsFolder).ConfigureAwait(true);
        if (folder is not null)
            ModelsFolder = folder;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (!_editor.IsDemo && Directory.Exists(ModelsFolder))
            FileManager.Reveal(ModelsFolder);
    }

    internal void Download(TranscriptionModelViewModel model)
    {
        if (!CanManageModels || !(model.IsNotInstalled || model.IsFailed))
            return;
        // A failed download resumes from its partial file.
        if (!model.IsFailed)
            model.Progress = 0;
        model.Error = null;
        model.ReportBytes(InstallPhase.Downloading, 0, null);
        model.State = ModelState.Downloading;
        if (_editor.IsDemo)
            StartDownloadTimer();
        else
            _ = InstallAsync(model, InstalledModels);
    }

    /// <summary>Downloads a model into the models folder; the row shows the progress.</summary>
    private async Task InstallAsync(TranscriptionModelViewModel model, ModelStore store)
    {
        using var cts = new CancellationTokenSource();
        model.RunningDownload = cts;
        var progress = new Progress<InstallProgress>(p =>
        {
            if (model.IsDownloading)
            {
                model.Progress = p.Fraction;
                model.ReportBytes(p.Phase, p.Received, p.Total);
            }
        });
        try
        {
            await Installer!.InstallAsync(model.Model, store, progress, cts.Token).ConfigureAwait(true);
            model.State = ModelState.Installed;
            model.Progress = 1;
            RefreshModelOptions(Model?.Value);
            RaiseTranscriptionChanged();
            RefreshSpace();
        }
        catch (OperationCanceledException)
        {
            // Cancelled with the row's ✕: nothing of it is kept.
            TryDelete(() => store.DeletePartial(model.Model));
            model.State = ModelState.NotInstalled;
            model.Progress = 0;
            RefreshSpace();
        }
        catch (ModelDownloadException e)
        {
            model.Error = e.Message;
            model.State = ModelState.Failed;
            _editor.ShowMessage($"Could not download {model.Id}: {e.Message}");
        }
        finally
        {
            model.RunningDownload = null;
        }
    }

    internal void Delete(TranscriptionModelViewModel model)
    {
        if (!CanManageModels)
            return;
        if (model.RunningDownload is { } running)
        {
            running.Cancel();
            return;
        }
        if (!_editor.IsDemo && !TryDelete(() => InstalledModels.Delete(model.Model)))
            return;
        model.State = ModelState.NotInstalled;
        model.Progress = 0;
        RefreshSpace();
        RefreshModelOptions(Model?.Value);
    }

    private bool TryDelete(Action delete)
    {
        try
        {
            delete();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _editor.ShowMessage("Could not delete the model: " + e.Message);
            return false;
        }
    }

    /// <summary>Advances simulated downloads (demo mode), 0.6 % every 150 ms like the prototype.</summary>
    public void TickDownloads()
    {
        bool finished = false;
        foreach (var m in Models.Where(m => m.IsDownloading))
        {
            m.Progress = Math.Min(1, m.Progress + 0.006);
            if (m.Progress >= 1)
            {
                m.State = ModelState.Installed;
                finished = true;
            }
        }
        if (finished)
            RefreshModelOptions(Model?.Value);
        if (!Models.Any(m => m.IsDownloading))
            StopDownloadTimer();
    }

    private void StartDownloadTimer()
    {
        if (_downloadTimer is not null || !Models.Any(m => m.IsDownloading))
            return;
        _downloadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => TickDownloads());
        _downloadTimer.Start();
    }

    private void StopDownloadTimer()
    {
        _downloadTimer?.Stop();
        _downloadTimer = null;
    }

    /// <summary>
    /// Marks the models that are fully installed in the models folder (downloads in progress stay as they are, and
    /// failed ones with <paramref name="keepFailures"/>), then which of the others fit on the disk.
    /// </summary>
    private void ScanModels(bool keepFailures)
    {
        if (!_editor.IsDemo)
        {
            RestoreCatalogModels();
            var store = InstalledModels;
            foreach (var m in Models.Where(m => !m.IsDownloading))
            {
                bool found = store.IsInstalled(m.Model);
                if (!found && keepFailures && m.IsFailed)
                    continue;
                m.State = found ? ModelState.Installed : ModelState.NotInstalled;
                m.Progress = found ? 1 : 0;
                m.Error = null;
            }
        }
        RefreshSpace();
    }

    /// <summary>Installed models of the chosen engine; "Best available" first when the engine is Auto.</summary>
    private void RefreshModelOptions(string? keep)
    {
        var installed = Models.Where(m => m.IsInstalled && (Engine == "Auto" || m.Engine == Engine)).ToList();
        var options = new List<ModelOption>();
        if (Engine == "Auto")
            options.Add(new ModelOption("best", "Best available — " + (installed.FirstOrDefault()?.Id ?? "none installed")));
        options.AddRange(installed.Select(m => new ModelOption(m.Id, m.Id)));
        bool wasLoading = _loading;
        _loading = true;
        ModelOptions = options;
        Model = options.FirstOrDefault(o => o.Value == keep) ?? options.FirstOrDefault();
        _loading = wasLoading;
    }

    partial void OnEngineChanged(string value)
    {
        foreach (var o in EngineOptions)
            o.IsSelected = o.Label == value;
        RefreshModelOptions(value == "Auto" ? "best" : null);
        Save();
    }

    partial void OnDeviceChanged(string value)
    {
        foreach (var o in DeviceOptions)
            o.IsSelected = o.Label == value;
        OnPropertyChanged(nameof(DeviceNote));
        Save();
    }

    partial void OnLanguageChanged(string value)
    {
        Save();
        RaiseTranscriptionChanged();
    }

    partial void OnModelChanged(ModelOption? value)
    {
        OnPropertyChanged(nameof(DeviceNote));
        Save();
        RaiseTranscriptionChanged();
    }

    partial void OnModelsFolderChanged(string value)
    {
        if (!_loading)
            ScanModels(keepFailures: false);
        RefreshModelOptions(Model?.Value);
        Save();
    }

    private void Save() => UpdateSettings(s => s);
}

/// <summary>An entry of the Model dropdown.</summary>
public sealed record ModelOption(string Value, string Label)
{
    public override string ToString() => Label;
}
