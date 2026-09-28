using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OurCut.Media.Ffmpeg;

namespace OurCut.Media.Playback;

/// <summary>libmpv could not be loaded, or a file could not be played.</summary>
public sealed class MpvException : Exception
{
    public MpvException()
    {
    }

    public MpvException(string message)
        : base(message)
    {
    }

    public MpvException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record MpvPlayerOptions
{
    /// <summary>mpv's <c>ao</c>; null picks the system default, "null" discards audio (tests, CI).</summary>
    public string? AudioOutput { get; init; }

    /// <summary>mpv's <c>hwdec</c>. Copy-back decoding works with every renderer.</summary>
    public string HardwareDecoding { get; init; } = "auto-copy";

    /// <summary>mpv's <c>audio-device</c> (<see cref="MpvPlayer.AudioDevices"/>); null for the system default.</summary>
    public string? AudioDevice { get; init; }
}

/// <summary>An audio output mpv can play to: its name ("wasapi/{…}", "pulse/…") and what the system calls it.</summary>
public sealed record AudioOutputDevice(string Name, string Description);

/// <summary>
/// How mpv plays the audio tracks that are not muted: one track at 0 dB directly (<c>aid</c>), none with
/// <c>aid=no</c>, anything else through <c>lavfi-complex</c>: a <c>volume</c> filter on each track whose gain is not
/// 0 dB, then <c>amix</c> when there are several.
/// </summary>
public readonly record struct AudioMix(string AudioTrack, string LavfiComplex)
{
    /// <param name="enabled">One entry per audio track, in the file's order.</param>
    /// <param name="gainsDb">Volume of each track in dB, in the same order; all 0 dB if null.</param>
    public static AudioMix For(IReadOnlyList<bool> enabled, IReadOnlyList<double>? gainsDb = null)
    {
        double Gain(int i) => gainsDb is not null && i < gainsDb.Count ? gainsDb[i] : 0;
        var on = Enumerable.Range(0, enabled.Count).Where(i => enabled[i]).ToList();
        if (on.Count == 0)
            return new AudioMix("no", "");
        if (on.Count == 1 && Gain(on[0]) == 0)
            return new AudioMix((on[0] + 1).ToString(CultureInfo.InvariantCulture), "");

        var graph = new StringBuilder();
        var inputs = new StringBuilder();
        foreach (int i in on)
        {
            string track = string.Create(CultureInfo.InvariantCulture, $"aid{i + 1}");
            if (Gain(i) == 0)
            {
                inputs.Append('[').Append(track).Append(']');
                continue;
            }
            // One track: its volume filter is the whole graph.
            string output = on.Count == 1 ? "ao" : string.Create(CultureInfo.InvariantCulture, $"g{i + 1}");
            graph.Append('[').Append(track).Append(']').Append(FfmpegText.VolumeFilter(Gain(i))).Append('[').Append(output).Append(']');
            if (on.Count > 1)
                graph.Append(';');
            inputs.Append('[').Append(output).Append(']');
        }
        if (on.Count > 1)
            graph.Append(inputs).Append(CultureInfo.InvariantCulture, $"amix=inputs={on.Count}:normalize=0[ao]");
        return new AudioMix("auto", graph.ToString());
    }
}

/// <summary>
/// One libmpv player. Everything that controls playback is sent with mpv's asynchronous API, so
/// callers (the UI thread, which may also render video) never wait for the player core; the state
/// comes back through observed properties on a background event thread, which raises
/// <see cref="StateChanged"/>.
/// </summary>
/// <remarks>
/// Seeks are exact (<c>hr-seek</c>). While a seek is in flight, <see cref="Position"/> keeps the
/// target and <see cref="IsSeeking"/> is true, and it stays the target after the seek lands until mpv
/// reports the next position, so the playhead does not jump back to stale positions (<see cref="SeekState"/>).
/// </remarks>
public sealed class MpvPlayer : IDisposable
{
    private const ulong SeekTag = 1UL << 62;
    private const ulong TimePosId = 1, PauseId = 2, DurationId = 3, EofId = 4, WidthId = 5, HeightId = 6, DecoderId = 7;

    private readonly Thread _eventThread;
    private readonly Lock _lock = new();
    private readonly Queue<string> _errors = new();
    private TaskCompletionSource? _loading;
    private string? _loadingPath;

    /// <summary>
    /// The file the app opened and has not unloaded. Unlike <see cref="LoadedPath"/> it survives the error mpv ends a
    /// file with when its video output is taken away, so a renderer switch reopens the right file.
    /// </summary>
    private string? _openPath;
    private readonly SeekState _seeks = new();
    private double _duration;
    private volatile bool _paused = true, _eof, _disposed;
    private int _videoWidth, _videoHeight;
    private string? _decoder;
    private MpvRenderer? _renderer;

    /// <summary>The audio tracks last set; null until then, and once another file is loaded, so they are sent again.</summary>
    private AudioMix? _audioMix;

    /// <summary>Checks that libmpv can be loaded.</summary>
    public static bool IsAvailable(out string? error) => MpvNative.TryLoad(out error);

    /// <exception cref="MpvException">libmpv is missing or could not start.</exception>
    public MpvPlayer(MpvPlayerOptions? options = null)
    {
        options ??= new MpvPlayerOptions();
        if (!MpvNative.TryLoad(out string? error))
            throw new MpvException(error!);
        Handle = MpvNative.mpv_create();
        if (Handle == IntPtr.Zero)
            throw new MpvException("mpv could not be created.");

        // Video goes through the render API into OurCut's own view (vo=libmpv) once a renderer is
        // attached; until then it is decoded and dropped, because vo=libmpv without a render context
        // fails the whole file. No mpv window, input or scripts.
        SetOption("vo", "null", required: true);
        SetOption("hwdec", options.HardwareDecoding);
        SetOption("keep-open", "always");
        SetOption("idle", "yes");
        SetOption("pause", "yes");
        SetOption("hr-seek", "yes");
        SetOption("hr-seek-framedrop", "no");
        SetOption("force-seekable", "yes");
        SetOption("audio-display", "no");
        SetOption("sub-auto", "no");
        SetOption("audio-file-auto", "no");
        SetOption("input-default-bindings", "no");
        SetOption("input-vo-keyboard", "no");
        SetOption("terminal", "no");
        SetOption("config", "no");
        SetOption("load-scripts", "no");
        SetOption("ytdl", "no");
        SetOption("audio-client-name", "OurCut");
        if (options.AudioOutput is { } ao)
            SetOption("ao", ao, required: true);
        if (options.AudioDevice is { } device)
            SetOption("audio-device", device);

        int status = MpvNative.mpv_initialize(Handle);
        if (status < 0)
        {
            MpvNative.mpv_terminate_destroy(Handle);
            throw new MpvException("mpv could not start: " + MpvNative.ErrorText(status));
        }
        Check(MpvNative.mpv_request_log_messages(Handle, "error"));
        Check(MpvNative.mpv_observe_property(Handle, TimePosId, "time-pos", MpvFormat.Double));
        Check(MpvNative.mpv_observe_property(Handle, PauseId, "pause", MpvFormat.Flag));
        Check(MpvNative.mpv_observe_property(Handle, DurationId, "duration", MpvFormat.Double));
        Check(MpvNative.mpv_observe_property(Handle, EofId, "eof-reached", MpvFormat.Flag));
        Check(MpvNative.mpv_observe_property(Handle, WidthId, "dwidth", MpvFormat.Int64));
        Check(MpvNative.mpv_observe_property(Handle, HeightId, "dheight", MpvFormat.Int64));
        Check(MpvNative.mpv_observe_property(Handle, DecoderId, "hwdec-current", MpvFormat.String));

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _eventThread.Start();
    }

    internal IntPtr Handle { get; }

    /// <summary>Playback position in seconds from the file's start; the seek target while seeking.</summary>
    public double Position => _seeks.Position;

    public double Duration => Volatile.Read(ref _duration);
    public bool IsPlaying => !_paused && !_eof && LoadedPath is not null;
    public bool IsEndReached => _eof;
    public bool IsSeeking => _seeks.IsSeeking;
    public (int Width, int Height) VideoSize => (Volatile.Read(ref _videoWidth), Volatile.Read(ref _videoHeight));

    /// <summary>
    /// How the video is decoded: the hardware decoder in use ("d3d11va-copy", "vaapi-copy"), "no" for the CPU, or null
    /// before a video is decoded. Read from mpv's events, so it is safe on any thread.
    /// </summary>
    public string? CurrentDecoder => Volatile.Read(ref _decoder);

    /// <summary>Why the last file stopped with an error.</summary>
    public string? LastError { get; private set; }

    /// <summary>The file that is loaded, or null.</summary>
    public string? LoadedPath { get; private set; }

    /// <summary>Raised on mpv's event thread when position, play state, duration or the loaded file changes.</summary>
    public event EventHandler? StateChanged;

    // ---- Control (asynchronous, safe from any thread) -----------------------------------

    /// <summary>Loads a file, paused, at <paramref name="startTime"/>. Completes when it is ready to play.</summary>
    /// <exception cref="MpvException">The file could not be played.</exception>
    public Task LoadAsync(string path, double startTime = 0)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _loading?.TrySetCanceled();
            _loading = tcs;
            _loadingPath = path;
            _openPath = path;
            _audioMix = null;
            _errors.Clear();
        }
        // "start" and "pause" apply to the next file; loadfile's own option syntax differs between versions.
        Command(0, "set", "pause", "yes");
        Command(0, "set", "start", Seconds(startTime));
        Command(0, "loadfile", path, "replace");
        return tcs.Task;
    }

    /// <summary>Stops playback and unloads the file.</summary>
    public void Unload()
    {
        lock (_lock)
        {
            _loading?.TrySetCanceled();
            _loading = null;
            _openPath = null;
        }
        LoadedPath = null;
        Command(0, "stop");
    }

    public void Play() => Command(0, "set", "pause", "no");

    public void Pause() => Command(0, "set", "pause", "yes");

    /// <summary>Exact seek to a source time.</summary>
    public void Seek(double time)
    {
        long generation = _seeks.Request(time);
        _eof = false;
        Command(SeekTag | (ulong)generation, "seek", Seconds(time), "absolute+exact");
    }

    /// <summary>One frame forward or back (pauses playback).</summary>
    public void StepFrame(bool forward) => Command(0, forward ? "frame-step" : "frame-back-step");

    /// <summary>Volume 0..1.</summary>
    public void SetVolume(double volume) => Command(0, "set", "volume", Seconds(Math.Clamp(volume, 0, 1) * 100));

    public void SetSpeed(double speed) => Command(0, "set", "speed", Seconds(Math.Clamp(speed, 0.01, 100)));

    /// <summary>
    /// Plays only the enabled audio tracks, each at its volume, mixed if there are several. mpv rebuilds the filter
    /// graph (a short gap in the sound) whenever it changes, so a call that changes nothing sends nothing.
    /// </summary>
    /// <param name="gainsDb">Volume of each track in dB; all 0 dB if null.</param>
    public void SetAudioTracks(IReadOnlyList<bool> enabled, IReadOnlyList<double>? gainsDb = null)
    {
        var mix = AudioMix.For(enabled, gainsDb);
        lock (_lock)
        {
            if (_audioMix == mix)
                return;
            _audioMix = mix;
        }
        // Leave the complex graph first so a single track can be picked, or enter it after.
        if (mix.LavfiComplex.Length == 0)
        {
            Command(0, "set", "lavfi-complex", "");
            Command(0, "set", "aid", mix.AudioTrack);
        }
        else
        {
            Command(0, "set", "aid", mix.AudioTrack);
            Command(0, "set", "lavfi-complex", mix.LavfiComplex);
        }
    }

    /// <summary>Plays to <paramref name="name"/>, one of <see cref="AudioDevices"/>; null for the system default.</summary>
    public void SetAudioDevice(string? name) => Command(0, "set", "audio-device", name ?? "auto");

    /// <summary>Changes <c>hwdec</c> while playing ("auto-copy", "no").</summary>
    public void SetHardwareDecoding(string hwdec) => Command(0, "set", "hwdec", hwdec);

    /// <summary>The audio outputs mpv sees now, without "auto" (the system default).</summary>
    public IReadOnlyList<AudioOutputDevice> AudioDevices() => ParseAudioDevices(GetPropertyString("audio-device-list"));

    /// <summary><c>audio-device-list</c> as mpv writes it: [{"name":"auto","description":"Autoselect device"}, …].</summary>
    internal static IReadOnlyList<AudioOutputDevice> ParseAudioDevices(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            var devices = new List<AudioOutputDevice>();
            foreach (var d in doc.RootElement.EnumerateArray())
            {
                if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } n
                    && n != "auto")
                {
                    string description = d.TryGetProperty("description", out var text) && text.GetString() is { Length: > 0 } t ? t : n;
                    devices.Add(new AudioOutputDevice(n, description));
                }
            }
            return devices;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Reads a property synchronously. Not for a thread that renders video.</summary>
    public string? GetPropertyString(string name)
    {
        IntPtr value = MpvNative.mpv_get_property_string(Handle, name);
        if (value == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            MpvNative.mpv_free(value);
        }
    }

    private static string Seconds(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private void Check(int status)
    {
        if (status >= 0)
            return;
        MpvNative.mpv_terminate_destroy(Handle);
        throw new MpvException("mpv could not start: " + MpvNative.ErrorText(status));
    }

    private void SetOption(string name, string value, bool required = false)
    {
        int status = MpvNative.mpv_set_option_string(Handle, name, value);
        if (status < 0 && required)
        {
            MpvNative.mpv_terminate_destroy(Handle);
            throw new MpvException($"mpv option {name}={value}: {MpvNative.ErrorText(status)}");
        }
    }

    private void Command(ulong reply, params string[] args)
    {
        if (_disposed)
            return;
        var pointers = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
                pointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            int status;
            unsafe
            {
                fixed (IntPtr* p = pointers)
                    status = MpvNative.mpv_command_async(Handle, reply, (IntPtr)p);
            }
            // Rejected before it ran (malformed or mpv shutting down): a seek will never report back.
            if (status < 0 && (reply & SeekTag) != 0)
                OnSeekReply((long)(reply & ~SeekTag), status);
        }
        finally
        {
            foreach (IntPtr p in pointers)
                Marshal.FreeCoTaskMem(p);
        }
    }

    // ---- Rendering ----------------------------------------------------------------------

    internal void Attach(MpvRenderer renderer)
    {
        lock (_lock)
        {
            if (_renderer is not null)
                throw new InvalidOperationException("mpv supports one renderer at a time.");
            _renderer = renderer;
        }
        SwitchVideoOutput("libmpv");
    }

    internal void Detach(MpvRenderer renderer)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_renderer, renderer))
                return;
            _renderer = null;
        }
        SwitchVideoOutput("null");
    }

    /// <summary>
    /// The video output is chosen when a file loads, so the loaded file is reopened where it was. A load
    /// still in flight finishes first (its caller is waiting for it).
    /// </summary>
    private void SwitchVideoOutput(string vo)
    {
        if (_disposed)
            return;
        Task pending;
        string? path;
        lock (_lock)
        {
            pending = _loading?.Task ?? Task.CompletedTask;
            path = _openPath;
        }
        // Where the video is now (a load in flight decides it when it lands).
        double? position = pending.IsCompleted ? Position : null;
        bool wasPlaying = IsPlaying;
        Command(0, "set", "vo", vo);
        if (path is null)
            return;
        _ = pending.ContinueWith(previous =>
        {
            // Unloaded, or another file opened meanwhile: that one is loaded with the new output already.
            if (_disposed || !string.Equals(Volatile.Read(ref _openPath), path, StringComparison.Ordinal))
                return;
            _ = LoadAsync(path, position ?? Position).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && wasPlaying)
                    Play();
                _ = t.Exception;
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    // ---- Events -------------------------------------------------------------------------

    private unsafe void EventLoop()
    {
        while (!_disposed)
        {
            var e = *(MpvEvent*)MpvNative.mpv_wait_event(Handle, -1);
            try
            {
                switch (e.EventId)
                {
                    case MpvEventId.Shutdown:
                        return;
                    case MpvEventId.PropertyChange:
                        OnProperty(e.ReplyUserdata, *(MpvEventProperty*)e.Data);
                        break;
                    case MpvEventId.CommandReply when (e.ReplyUserdata & SeekTag) != 0:
                        OnSeekReply((long)(e.ReplyUserdata & ~SeekTag), e.Error);
                        break;
                    case MpvEventId.PlaybackRestart:
                        OnPlaybackRestart();
                        break;
                    case MpvEventId.FileLoaded:
                        OnFileLoaded();
                        break;
                    case MpvEventId.EndFile:
                        OnEndFile(*(MpvEventEndFile*)e.Data);
                        break;
                    case MpvEventId.LogMessage:
                        OnLog(*(MpvEventLogMessage*)e.Data);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A subscriber failed; the event loop must keep running.
                System.Diagnostics.Debug.WriteLine("mpv event handler failed: " + ex);
            }
        }
    }

    private unsafe void OnProperty(ulong id, MpvEventProperty property)
    {
        bool has = property.Format != MpvFormat.None && property.Data != IntPtr.Zero;
        switch (id)
        {
            case TimePosId:
                _seeks.Reported(has ? *(double*)property.Data : 0);
                if (_seeks.IsSeeking)
                    return;
                break;
            case PauseId:
                _paused = !has || *(int*)property.Data != 0;
                break;
            case DurationId:
                Volatile.Write(ref _duration, has ? *(double*)property.Data : 0);
                break;
            case EofId:
                _eof = has && *(int*)property.Data != 0;
                break;
            case WidthId:
                Volatile.Write(ref _videoWidth, has ? (int)*(long*)property.Data : 0);
                break;
            case HeightId:
                Volatile.Write(ref _videoHeight, has ? (int)*(long*)property.Data : 0);
                break;
            case DecoderId:
                Volatile.Write(ref _decoder, has && property.Format == MpvFormat.String ? Marshal.PtrToStringUTF8(*(IntPtr*)property.Data) : null);
                return;
            default:
                return;
        }
        RaiseChanged();
    }

    private void OnSeekReply(long generation, int error)
    {
        // A seek that failed (nothing loaded) produces no playback restart.
        _seeks.Replied(generation, failed: error < 0);
        if (error < 0)
            RaiseChanged();
    }

    private void OnPlaybackRestart()
    {
        // Settles the seeks replied to so far; one still in flight keeps the target as the position.
        _seeks.Restarted();
        RaiseChanged();
    }

    private void OnFileLoaded()
    {
        TaskCompletionSource? loading;
        lock (_lock)
        {
            loading = _loading;
            _loading = null;
            LoadedPath = _loadingPath;
        }
        _eof = false;
        loading?.TrySetResult();
        RaiseChanged();
    }

    private void OnEndFile(MpvEventEndFile end)
    {
        if (end.Reason != MpvEndFileReason.Error)
            return;
        TaskCompletionSource? loading;
        string detail;
        lock (_lock)
        {
            loading = _loading;
            _loading = null;
            detail = _errors.Count > 0 ? string.Join(" ", _errors) : MpvNative.ErrorText(end.Error);
        }
        LoadedPath = null;
        LastError = detail;
        loading?.TrySetException(new MpvException("mpv could not play the file: " + detail));
        RaiseChanged();
    }

    private void OnLog(MpvEventLogMessage message)
    {
        string text = (Marshal.PtrToStringUTF8(message.Text) ?? "").Trim();
        if (text.Length == 0)
            return;
        lock (_lock)
        {
            _errors.Enqueue(text);
            while (_errors.Count > 5)
                _errors.Dequeue();
        }
    }

    private void RaiseChanged()
    {
        if (!_disposed)
            StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        MpvRenderer? renderer;
        lock (_lock)
        {
            renderer = _renderer;
            _loading?.TrySetCanceled();
        }
        // The render context has to go before the core, and no thread may wait for events while the
        // handle is destroyed: wake the event loop, let it see _disposed, then terminate.
        renderer?.Dispose();
        MpvNative.mpv_wakeup(Handle);
        _eventThread.Join();
        MpvNative.mpv_terminate_destroy(Handle);
    }
}
