using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HighlightCut.App.Services;
using HighlightCut.Media.Playback;

namespace HighlightCut.App.ViewModels;

// Settings → Playback: decoding, renderer, audio output, jump length, volume and speed.
public sealed partial class SettingsViewModel
{
    private const string SystemDefaultDevice = "System default";
    private static readonly double[] JumpLengths = [0.5, 1, 2, 5];

    private DispatcherTimer? _volumeSpeedTimer;

    /// <summary>The player's audio outputs: mpv's name for each description the list shows.</summary>
    private IReadOnlyList<AudioOutputDevice> _audioDevices = [];

    private ChoiceSet<HardwareDecodingMode>? _hardwareDecodingChoices;
    private ChoiceSet<HardwareDecodingMode> HardwareDecodingChoices => _hardwareDecodingChoices ??= new(
        [(HardwareDecodingMode.Auto, "Auto"), (HardwareDecodingMode.Off, "Off")], v => HardwareDecoding = v, HardwareDecoding);
    public IReadOnlyList<ChoiceOption> HardwareDecodingOptions => HardwareDecodingChoices.Options;

    private ChoiceSet<VideoRendererMode>? _rendererChoices;
    private ChoiceSet<VideoRendererMode> RendererChoices => _rendererChoices ??= new(
        [(VideoRendererMode.Auto, "Auto (GPU)"), (VideoRendererMode.Software, "Software")], v => Renderer = v, Renderer);
    public IReadOnlyList<ChoiceOption> RendererOptions => RendererChoices.Options;

    private ChoiceSet<double>? _jumpChoices;
    private ChoiceSet<double> JumpChoices => _jumpChoices ??= new(
        [(0.5, "0.5 s"), (1, "1 s"), (2, "2 s"), (5, "5 s")], v => JumpSeconds = v, JumpSeconds);
    public IReadOnlyList<ChoiceOption> JumpOptions => JumpChoices.Options;

    [ObservableProperty]
    public partial HardwareDecodingMode HardwareDecoding { get; set; } = HardwareDecodingMode.Auto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesSoftwareRenderer))]
    public partial VideoRendererMode Renderer { get; set; } = VideoRendererMode.Auto;

    /// <summary>The video view draws without OpenGL (Renderer: Software).</summary>
    public bool UsesSoftwareRenderer => Renderer == VideoRendererMode.Software;

    /// <summary>"System default", then the player's audio outputs by description.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> AudioDevices { get; private set; } = [SystemDefaultDevice];

    [ObservableProperty]
    public partial string AudioDevice { get; set; } = SystemDefaultDevice;

    [ObservableProperty]
    public partial double JumpSeconds { get; set; } = 1;

    [ObservableProperty]
    public partial bool RememberVolumeAndSpeed { get; set; } = true;


    partial void OnHardwareDecodingChanged(HardwareDecodingMode value)
    {
        _hardwareDecodingChoices?.Select(value);
        _editor.Player?.SetHardwareDecoding(value);
        SavePlayback();
    }

    partial void OnRendererChanged(VideoRendererMode value)
    {
        _rendererChoices?.Select(value);
        // The video view follows UsesSoftwareRenderer.
        SavePlayback();
    }

    partial void OnAudioDeviceChanged(string value)
    {
        // The list clears its selection while its items are replaced.
        if (value is null)
            return;
        _editor.Player?.SetAudioDevice(DeviceName(value));
        SavePlayback();
    }

    partial void OnJumpSecondsChanged(double value)
    {
        _jumpChoices?.Select(value);
        _editor.JumpSeconds = value;
        SavePlayback();
    }

    partial void OnRememberVolumeAndSpeedChanged(bool value)
    {
        if (!_loading)
            UpdateSettings(s => s with { Playback = WithVolumeAndSpeed(ToPlaybackSettings()) });
    }

    private PlaybackSettings WithVolumeAndSpeed(PlaybackSettings s) => RememberVolumeAndSpeed
        ? s with { Volume = _editor.Volume, Speed = _editor.Speed }
        : s with { Volume = null, Speed = null };

    private void InitPlayback()
    {
        // A device plugged in since shows up the next time the dialog opens.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsOpen) && IsOpen)
                RefreshAudioDevices();
        };
        _editor.PropertyChanged += OnVolumeOrSpeedChanged;
    }

    /// <summary>Lists the player's audio outputs; the chosen one stays listed even when it is unplugged.</summary>
    private void RefreshAudioDevices()
    {
        if (_editor.IsDemo || _editor.Player is not { } player)
            return;
        string chosen = AudioDevice;
        _audioDevices = player.AudioDevices();
        var shown = new List<string> { SystemDefaultDevice };
        shown.AddRange(_audioDevices.Select(d => d.Description).Distinct(StringComparer.Ordinal));
        if (!shown.Contains(chosen))
            shown.Add(chosen);
        bool wasLoading = _loading;
        _loading = true;
        AudioDevices = shown;
        AudioDevice = chosen;
        _loading = wasLoading;
    }

    /// <summary>mpv's name for a device the list shows; a device that is not plugged in is shown by its name.</summary>
    private string? DeviceName(string shown) => shown == SystemDefaultDevice ? null
        : _audioDevices.FirstOrDefault(d => d.Description == shown)?.Name ?? shown;

    private string DeviceShown(string? name) => name is null ? SystemDefaultDevice
        : _audioDevices.FirstOrDefault(d => d.Name == name)?.Description ?? name;

    // A slider drag is saved once, after it stops.
    private void OnVolumeOrSpeedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(EditorViewModel.Volume) or nameof(EditorViewModel.Speed))
            || !RememberVolumeAndSpeed || _loading || _editor.IsDemo)
            return;
        if (_volumeSpeedTimer is null)
        {
            _volumeSpeedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            _volumeSpeedTimer.Tick += (_, _) =>
            {
                _volumeSpeedTimer.Stop();
                UpdateSettings(s => s with { Playback = WithVolumeAndSpeed(ToPlaybackSettings()) });
            };
        }
        _volumeSpeedTimer.Stop();
        _volumeSpeedTimer.Start();
    }

    private void SavePlayback() => UpdateSettings(s => s with { Playback = ToPlaybackSettings() });

    /// <summary>The playback settings shown; the remembered volume and speed stay as they were saved.</summary>
    internal PlaybackSettings ToPlaybackSettings()
    {
        var stored = _settings.Playback;
        return new(HardwareDecoding, Renderer, DeviceName(AudioDevice), JumpSeconds, RememberVolumeAndSpeed, stored?.Volume, stored?.Speed);
    }

    /// <summary>Shows <paramref name="s"/>; a value the dialog does not offer reads as its default.</summary>
    internal void LoadPlayback(PlaybackSettings? s)
    {
        s ??= new();
        HardwareDecoding = Enum.IsDefined(s.HardwareDecoding) ? s.HardwareDecoding : HardwareDecodingMode.Auto;
        Renderer = Enum.IsDefined(s.Renderer) ? s.Renderer : VideoRendererMode.Auto;
        RefreshAudioDevices();
        string device = DeviceShown(string.IsNullOrWhiteSpace(s.AudioDevice) ? null : s.AudioDevice);
        // A device that is not plugged in now stays chosen, so the list shows it.
        if (!AudioDevices.Contains(device))
            AudioDevices = [.. AudioDevices, device];
        AudioDevice = device;
        JumpSeconds = JumpLengths.Contains(s.JumpSeconds) ? s.JumpSeconds : 1;
        RememberVolumeAndSpeed = s.RememberVolumeAndSpeed;
        _editor.JumpSeconds = JumpSeconds;
        if (!s.RememberVolumeAndSpeed)
            return;
        if (s.Volume is >= 0 and <= 1 and var volume)
            _editor.Volume = volume;
        if (s.Speed is (0.5 or 1 or 1.5 or 2) and var speed)
            _editor.Speed = speed;
    }
}
