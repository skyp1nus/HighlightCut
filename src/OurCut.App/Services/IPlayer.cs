using Avalonia.Threading;
using OurCut.Media.Playback;

namespace OurCut.App.Services;

/// <summary>
/// Plays the source video for the editor. The real one is libmpv (<see cref="MpvPlaybackEngine"/>);
/// tests use a fake. Every call returns immediately; the state comes back through <see cref="StateChanged"/>.
/// </summary>
public interface IPlayer : IDisposable
{
    /// <summary>Playback position in seconds (the seek target while a seek is in flight).</summary>
    double Position { get; }

    bool IsPlaying { get; }

    /// <summary>True while a seek has not landed yet; positions reported meanwhile are stale.</summary>
    bool IsSeeking { get; }

    /// <summary>Raised on the UI thread when the position, play state or loaded file changes.</summary>
    event EventHandler? StateChanged;

    /// <exception cref="MpvException">The file cannot be played.</exception>
    Task LoadAsync(string path);

    void Unload();
    void Play();
    void Pause();

    /// <summary>Frame-exact seek.</summary>
    void Seek(double time);

    void StepFrame(bool forward);

    /// <summary>Volume 0..1.</summary>
    void SetVolume(double volume);

    void SetSpeed(double speed);

    /// <summary>
    /// Which audio tracks are heard and how loud (one entry per track, in the file's order): <paramref name="gainsDb"/>
    /// is each track's volume in dB.
    /// </summary>
    void SetAudioTracks(IReadOnlyList<bool> enabled, IReadOnlyList<double> gainsDb);

    /// <summary>The audio outputs the system has now; empty when they cannot be listed.</summary>
    IReadOnlyList<AudioOutputDevice> AudioDevices();

    /// <summary>Plays to a device of <see cref="AudioDevices"/>; null for the system default.</summary>
    void SetAudioDevice(string? name);

    /// <summary>Settings → Playback → Hardware decoding, from now on.</summary>
    void SetHardwareDecoding(HardwareDecodingMode mode);

    /// <summary>How the open video is decoded ("d3d11va-copy", or "no" on the CPU); null when unknown.</summary>
    string? CurrentDecoder => null;
}

/// <summary><see cref="IPlayer"/> on libmpv. The video view attaches a renderer to <see cref="Mpv"/>.</summary>
public sealed class MpvPlaybackEngine : IPlayer
{
    private int _changePending;
    private bool _disposed;

    public MpvPlaybackEngine(MpvPlayerOptions? options = null)
    {
        Mpv = new MpvPlayer(options);
        Mpv.StateChanged += (_, _) => PostChanged();
    }

    /// <summary>Creates the engine, or returns null with the reason when libmpv is not available.</summary>
    public static MpvPlaybackEngine? TryCreate(out string? error, MpvPlayerOptions? options = null)
    {
        try
        {
            var engine = new MpvPlaybackEngine(options);
            error = null;
            return engine;
        }
        catch (MpvException e)
        {
            error = e.Message;
            return null;
        }
    }

    public MpvPlayer Mpv { get; }
    public double Position => Mpv.Position;
    public bool IsPlaying => Mpv.IsPlaying;
    public bool IsSeeking => Mpv.IsSeeking;

    public event EventHandler? StateChanged;

    public Task LoadAsync(string path) => Mpv.LoadAsync(path);
    public void Unload() => Mpv.Unload();
    public void Play() => Mpv.Play();
    public void Pause() => Mpv.Pause();
    public void Seek(double time) => Mpv.Seek(time);
    public void StepFrame(bool forward) => Mpv.StepFrame(forward);
    public void SetVolume(double volume) => Mpv.SetVolume(volume);
    public void SetSpeed(double speed) => Mpv.SetSpeed(speed);
    public void SetAudioTracks(IReadOnlyList<bool> enabled, IReadOnlyList<double> gainsDb) => Mpv.SetAudioTracks(enabled, gainsDb);
    public IReadOnlyList<AudioOutputDevice> AudioDevices() => Mpv.AudioDevices();
    public void SetAudioDevice(string? name) => Mpv.SetAudioDevice(name);
    public void SetHardwareDecoding(HardwareDecodingMode mode) => Mpv.SetHardwareDecoding(PlaybackSettings.MpvHardwareDecoding(mode));
    public string? CurrentDecoder => Mpv.LoadedPath is null ? null : Mpv.CurrentDecoder;

    /// <summary>mpv reports every frame; the UI hears about it at most once per dispatcher pass.</summary>
    private void PostChanged()
    {
        if (Interlocked.Exchange(ref _changePending, 1) == 1)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _changePending, 0);
            if (!_disposed)
                StateChanged?.Invoke(this, EventArgs.Empty);
        }, DispatcherPriority.Input);
    }

    public void Dispose()
    {
        _disposed = true;
        Mpv.Dispose();
    }
}
