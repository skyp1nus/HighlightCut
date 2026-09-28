using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HighlightCut.App.Controls;
using HighlightCut.App.Demo;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;
using HighlightCut.Media;
using HighlightCut.Media.Caching;
using HighlightCut.Media.Playback;
using HighlightCut.Media.Tools;

namespace HighlightCut.App.Tests;

/// <summary>Records what the editor asks of the player and lets tests move it.</summary>
internal sealed class FakePlayer : IPlayer
{
    public List<string> Calls { get; } = [];
    public string? LoadedPath { get; private set; }
    public Exception? LoadError { get; set; }
    public double Position { get; set; }
    public bool IsPlaying { get; set; }
    public bool IsSeeking { get; set; }

    public event EventHandler? StateChanged;

    public void Report(double position, bool playing = false, bool seeking = false)
    {
        Position = position;
        IsPlaying = playing;
        IsSeeking = seeking;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task LoadAsync(string path)
    {
        Calls.Add("load " + Path.GetFileName(path));
        if (LoadError is { } e)
            return Task.FromException(e);
        LoadedPath = path;
        return Task.CompletedTask;
    }

    public void Unload() => Calls.Add("unload");
    public void Play() => Calls.Add("play");
    public void Pause() => Calls.Add("pause");
    public void Seek(double time) => Calls.Add(FormattableString.Invariant($"seek {time:0.###}"));
    public void StepFrame(bool forward) => Calls.Add(forward ? "step +1" : "step -1");
    public void SetVolume(double volume) => Calls.Add(FormattableString.Invariant($"volume {volume:0.##}"));
    public void SetSpeed(double speed) => Calls.Add(FormattableString.Invariant($"speed {speed:0.##}"));
    public void SetAudioTracks(IReadOnlyList<bool> enabled, IReadOnlyList<double> gainsDb) =>
        Calls.Add("tracks " + string.Concat(enabled.Select(e => e ? '1' : '0'))
            + (gainsDb.Any(g => g != 0) ? " gains " + string.Join(",", gainsDb.Select(g => g.ToString(System.Globalization.CultureInfo.InvariantCulture))) : ""));
    public IReadOnlyList<HighlightCut.Media.Playback.AudioOutputDevice> Devices { get; set; } = [];
    public IReadOnlyList<HighlightCut.Media.Playback.AudioOutputDevice> AudioDevices() => Devices;
    public void SetAudioDevice(string? name) => Calls.Add("device " + (name ?? "default"));
    public void SetHardwareDecoding(HardwareDecodingMode mode) => Calls.Add("hwdec " + mode);
    public void Dispose() => Calls.Add("dispose");
}

/// <summary>A real (not placeholder) file of 10 s at 25 fps with two audio tracks.</summary>
internal sealed class FakeMediaOpener : IMediaOpener
{
    private sealed class Preview : IMediaPreview
    {
        public double Duration => 10;
        public double FrameRate => 25;
        public IReadOnlyList<double> Keyframes => [0, 2, 4, 6, 8];
        public int AudioStreamCount => 2;
        public bool IsPlaceholder => false;
        public string? Activity => null;
        public string? AnalysisError => null;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public double AudioPeak(int stream, double startTime, double endTime) => 0.5;

        /// <summary>Mic at −30 dB, Music at −10 dB.</summary>
        public HighlightCut.Media.Analysis.AudioLevel? MeasureAudio(int stream) =>
            stream == 0 ? new(-30, -20) : new(-10, -2);

        public void DrawFrame(DrawingContext context, Rect rect, double time, FrameLook look, int variant) =>
            context.FillRectangle(Brushes.DimGray, rect);
    }

    public Task<OpenedMedia> OpenAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(new OpenedMedia(
            new HighlightCut.Core.Model.SourceMedia(path, 10, 25, [new(1, "Mic"), new(2, "Music")]), new Preview(), "fake.mp4"));
}

public class PlaybackTests
{
    private static async Task<(EditorViewModel Editor, FakePlayer Player)> OpenAsync(Exception? loadError = null)
    {
        var player = new FakePlayer { LoadError = loadError };
        var editor = App.CreateEditor(null, new FakeMediaOpener(), player: player);
        await editor.OpenMediaAsync("/videos/fake.mp4");
        Dispatcher.UIThread.RunJobs();
        return (editor, player);
    }

    [AvaloniaFact]
    public async Task Opening_a_file_loads_it_into_the_player_with_the_editor_settings()
    {
        var (editor, player) = await OpenAsync();
        Assert.True(editor.HasPlayback);
        Assert.Equal(["load fake.mp4", "volume 0.7", "speed 1", "tracks 11"], player.Calls);
    }

    [AvaloniaFact]
    public async Task Play_and_pause_go_to_the_player()
    {
        var (editor, player) = await OpenAsync();
        player.Calls.Clear();

        editor.TogglePlay();
        Assert.True(editor.IsPlaying);
        editor.TogglePlay();
        Assert.False(editor.IsPlaying);

        Assert.Equal(["play", "pause"], player.Calls);
    }

    [AvaloniaFact]
    public async Task Play_range_pauses_at_its_end_but_a_seek_past_it_is_kept()
    {
        var (editor, player) = await OpenAsync();
        player.Calls.Clear();

        editor.PlayRange(2, 3);
        player.Report(3.2, playing: true);
        Dispatcher.UIThread.RunJobs();
        Assert.False(editor.IsPlaying);
        Assert.Equal(3, editor.Time);
        Assert.Equal(["seek 2", "play", "pause", "seek 3"], player.Calls);

        player.Calls.Clear();
        editor.PlayRange(2, 3);
        player.Report(2.5, playing: true);
        editor.SetTime(8);
        Dispatcher.UIThread.RunJobs();
        player.Report(8.1, playing: true);
        Assert.True(editor.IsPlaying);
        Assert.Equal(8.1, editor.Time);
        Assert.Equal(["seek 2", "play", "seek 8"], player.Calls);
    }

    [AvaloniaFact]
    public async Task The_playhead_follows_the_player_without_seeking_back()
    {
        var (editor, player) = await OpenAsync();
        player.Calls.Clear();

        player.Report(3.5, playing: true);

        Assert.Equal(3.5, editor.Time);
        Assert.True(editor.IsPlaying);
        Assert.Empty(player.Calls);
    }

    [AvaloniaFact]
    public async Task Moving_the_playhead_seeks_and_stale_positions_are_ignored_until_it_lands()
    {
        var (editor, player) = await OpenAsync();
        player.Calls.Clear();

        editor.ScrubTo(6.2, select: false);
        Assert.Equal(["seek 6.2"], player.Calls);

        player.Report(1.0, seeking: true);
        Assert.Equal(6.2, editor.Time);

        player.Report(6.2);
        Assert.Equal(6.2, editor.Time);
    }

    [AvaloniaFact]
    public async Task Frame_steps_are_done_by_the_player()
    {
        var (editor, player) = await OpenAsync();
        player.Calls.Clear();

        editor.StepForwardCommand.Execute(null);
        editor.StepBackCommand.Execute(null);

        Assert.Equal(["step +1", "step -1"], player.Calls);
        player.Report(0.04);
        Assert.Equal(0.04, editor.Time);
    }

    [AvaloniaFact]
    public async Task Volume_speed_and_muted_lanes_reach_the_player()
    {
        var (editor, player) = await OpenAsync();
        player.Calls.Clear();

        editor.Volume = 0.25;
        editor.CycleSpeedCommand.Execute(null);
        editor.AudioLanes[0].IsMuted = true;

        Assert.Equal(["volume 0.25", "speed 1.5", "tracks 01"], player.Calls);
    }

    [AvaloniaFact]
    public async Task Play_at_the_end_starts_over()
    {
        var (editor, player) = await OpenAsync();
        player.Report(10);
        player.Calls.Clear();

        editor.TogglePlay();

        Assert.Equal(["seek 0", "play"], player.Calls);
        Assert.Equal(0, editor.Time);
    }

    [AvaloniaFact]
    public async Task Opening_another_file_or_the_sample_unloads_the_player()
    {
        var (editor, player) = await OpenAsync();
        player.Calls.Clear();

        DemoScenario.OpenSample(editor);

        Assert.False(editor.HasPlayback);
        Assert.Equal(["unload"], player.Calls);
    }

    [AvaloniaFact]
    public async Task A_file_the_player_cannot_play_is_still_edited_with_simulated_playback()
    {
        var (editor, player) = await OpenAsync(new MpvException("unsupported codec"));

        Assert.True(editor.HasFile);
        Assert.False(editor.HasPlayback);
        Assert.Contains("unsupported codec", editor.StatusMessage, StringComparison.Ordinal);

        player.Calls.Clear();
        editor.TogglePlay();
        Assert.True(editor.IsPlaying);
        Assert.Empty(player.Calls);
        editor.TogglePlay();
    }

    [AvaloniaFact]
    public async Task Playback_settings_reach_the_running_player()
    {
        var (editor, player) = await OpenAsync();
        player.Devices = [new("wasapi/{1}", "Speakers (Realtek)"), new("wasapi/{2}", "Headphones")];
        var settings = editor.Settings;

        settings.Open();

        Assert.Equal(["System default", "Speakers (Realtek)", "Headphones"], settings.AudioDevices);
        settings.AudioDevice = "Headphones";
        Assert.Equal("device wasapi/{2}", player.Calls[^1]);
        Assert.Equal("wasapi/{2}", settings.Current.Playback!.AudioDevice);
        settings.AudioDevice = "System default";
        Assert.Equal("device default", player.Calls[^1]);
        Assert.Null(settings.Current.Playback!.AudioDevice);

        settings.HardwareDecoding = HardwareDecodingMode.Off;
        Assert.Equal("hwdec Off", player.Calls[^1]);
        settings.Close();

        // A saved device that is unplugged stays chosen, shown by its name; plugged in again, by its description.
        player.Devices = [player.Devices[0]];
        settings.Load(AppSettings.Default with { Playback = new PlaybackSettings(AudioDevice: "wasapi/{2}") });
        Assert.Equal("wasapi/{2}", settings.AudioDevice);
        Assert.Equal(["System default", "Speakers (Realtek)", "wasapi/{2}"], settings.AudioDevices);
        Assert.Equal("device wasapi/{2}", player.Calls[^1]);
        player.Devices = [.. player.Devices, new("wasapi/{2}", "Headphones")];
        settings.Load(AppSettings.Default with { Playback = new PlaybackSettings(AudioDevice: "wasapi/{2}") });
        Assert.Equal("Headphones", settings.AudioDevice);
    }

    [AvaloniaFact]
    public void The_video_view_follows_the_renderer_setting()
    {
        var editor = App.CreateEditor(null, player: new FakePlayer());
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        var view = window.GetVisualDescendants().OfType<VideoView>().Single();
        Assert.False(view.SoftwareOnly);

        editor.Settings.Renderer = VideoRendererMode.Software;
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.SoftwareOnly);
        Assert.Equal(VideoRendererMode.Software, editor.Settings.Current.Playback!.Renderer);
        window.Close();
    }

    [AvaloniaFact]
    public void Without_a_picture_the_editor_says_so_and_diagnostics_name_the_output()
    {
        var editor = App.CreateEditor(null, player: new FakePlayer());
        Assert.Contains("Video output not started · decoder —", editor.Settings.DiagnosticsText(), StringComparison.Ordinal);
        Assert.Contains("Analysis —", editor.Settings.DiagnosticsText(), StringComparison.Ordinal);

        editor.VideoOutput = "OpenGL · ANGLE (AMD, AMD Radeon RX 6700 XT Direct3D11 vs_5_0 ps_5_0)";
        Assert.Contains("Video output OpenGL · ANGLE (AMD, AMD Radeon RX 6700 XT", editor.Settings.DiagnosticsText(), StringComparison.Ordinal);
        Assert.NotEqual("No video picture", editor.StatusMessage?[..16]);

        editor.VideoOutput = "none: mpv could not create a renderer: unsupported";
        Assert.Equal("No video picture (mpv could not create a renderer: unsupported). The player shows thumbnails only.", editor.StatusMessage);
    }

    [AvaloniaFact]
    public void Without_a_file_the_video_view_shows_nothing()
    {
        var editor = App.CreateEditor(null, player: new FakePlayer());
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<VideoView>().Single();
        Assert.False(view.IsEffectivelyVisible);
        Assert.Null(view.Mode);
        window.Close();
    }
}

/// <summary>
/// Real playback with libmpv in the headless window (software rendering, audio discarded).
/// Skipped without ffmpeg (to make the sample) or libmpv.
/// </summary>
public sealed class RealPlaybackTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-play").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Temp folder; the OS cleans it up.
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task PumpUntil(Func<bool> done, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Assert.True(done());
    }

    [AvaloniaFact]
    public async Task A_video_plays_in_the_player_panel()
    {
        Assert.SkipWhen(NativeTools.FindTool("ffmpeg") is null || NativeTools.FindTool("ffprobe") is null, "ffmpeg is not installed.");
        Assert.SkipUnless(MpvPlayer.IsAvailable(out string? mpvError), mpvError ?? "");
        string video = Path.Combine(_dir, "bars.mp4");
        await Task.Run(() => ToolProcess.RunAsync("ffmpeg",
        [
            "-v", "error", "-f", "lavfi", "-i", "smptebars=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=440",
            "-t", "4", "-c:v", "libx264", "-preset", "veryfast", "-g", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", video,
        ], null, Ct), Ct);

        VideoView.PreferOpenGl = false;
        using var engine = new MpvPlaybackEngine(new MpvPlayerOptions { AudioOutput = "null", HardwareDecoding = "no" });
        var editor = App.CreateEditor(null, new FfmpegMediaOpener(new MediaCache(Path.Combine(_dir, "cache"))), player: engine);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();

        await editor.OpenMediaAsync(video);
        await PumpUntil(() => editor.HasPlayback);
        var view = window.GetVisualDescendants().OfType<VideoView>().Single();
        Assert.Equal("software", view.Mode);

        // Seek, then play for a moment.
        editor.SetTime(1.0);
        await PumpUntil(() => !engine.IsSeeking && Math.Abs(engine.Position - 1.0) < 0.01);
        editor.TogglePlay();
        await PumpUntil(() => editor.Time > 1.3);
        editor.TogglePlay();
        await PumpUntil(() => !editor.IsPlaying);

        // The frame is drawn: SMPTE bars have a bright yellow second bar.
        await PumpUntil(() => HasYellow(window), 5);
        using (var frame = window.CaptureRenderedFrame())
            frame!.Save(Path.Combine(Screenshots.Directory, "playback.png"), new PngBitmapEncoderOptions());

        Assert.Equal("software", editor.VideoOutput);
        Assert.Contains("decoder no", editor.Settings.DiagnosticsText(), StringComparison.Ordinal);

        // Settings → Playback while the video is open: the view is rebuilt and draws again; decoding and the
        // audio device change in mpv.
        var before = view.Child;
        editor.Settings.Renderer = VideoRendererMode.Software;
        await PumpUntil(() => view.Child is not null && !ReferenceEquals(view.Child, before));
        editor.SetTime(2.0);
        await PumpUntil(() => HasYellow(window), 5);
        editor.Settings.HardwareDecoding = HardwareDecodingMode.Off;
        await PumpUntil(() => engine.Mpv.GetPropertyString("hwdec") == "no");
        editor.Settings.Open();
        Assert.Equal("System default", editor.Settings.AudioDevices[0]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_software_view_waits_for_the_renderer_it_replaces_to_let_go()
    {
        Assert.SkipWhen(NativeTools.FindTool("ffmpeg") is null || NativeTools.FindTool("ffprobe") is null, "ffmpeg is not installed.");
        Assert.SkipUnless(MpvPlayer.IsAvailable(out string? mpvError), mpvError ?? "");
        string video = Path.Combine(_dir, "bars.mp4");
        await Task.Run(() => ToolProcess.RunAsync("ffmpeg",
        [
            "-v", "error", "-f", "lavfi", "-i", "smptebars=size=640x360:rate=30", "-t", "2", "-c:v", "libx264", "-preset", "veryfast",
            "-pix_fmt", "yuv420p", "-y", video,
        ], null, Ct), Ct);
        VideoView.PreferOpenGl = false;
        using var engine = new MpvPlaybackEngine(new MpvPlayerOptions { AudioOutput = "null", HardwareDecoding = "no" });
        // Stands in for an OpenGL renderer that keeps drawing until it is released, a moment after its view is replaced.
        var holder = new MpvSoftwareRenderer(engine.Mpv);
        using var stopHolder = new CancellationTokenSource();
        var holding = Task.Run(() =>
        {
            var pixels = Marshal.AllocHGlobal(64 * 36 * 4);
            try
            {
                while (!stopHolder.IsCancellationRequested)
                {
                    if (holder.HasNewFrame())
                        holder.Render(pixels, 64, 36, 64 * 4);
                    Thread.Sleep(10);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pixels);
            }
        }, CancellationToken.None);
        var editor = App.CreateEditor(null, new FfmpegMediaOpener(new MediaCache(Path.Combine(_dir, "cache"))), player: engine);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();

        await editor.OpenMediaAsync(video);
        await PumpUntil(() => editor.HasPlayback);
        await Task.Delay(300, Ct);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(editor.VideoOutput);
        await stopHolder.CancelAsync();
        await holding;
        holder.Dispose();

        await PumpUntil(() => editor.VideoOutput == "software");
        await PumpUntil(() => HasYellow(window), 10);
        Assert.Null(editor.StatusMessage is { } m && m.StartsWith("No video picture", StringComparison.Ordinal) ? m : null);
        window.Close();
    }

    /// <summary>Looks for the SMPTE yellow in the upper part of the player area.</summary>
    private static bool HasYellow(MainWindow window)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        if (frame is null)
            return false;
        int w = frame.PixelSize.Width, h = frame.PixelSize.Height, stride = w * 4;
        var pixels = new byte[stride * h];
        unsafe
        {
            fixed (byte* p = pixels)
                frame.CopyPixels(new PixelRect(frame.PixelSize), (IntPtr)p, pixels.Length, stride);
        }
        for (int y = h / 6; y < h / 2; y += 7)
        {
            for (int x = 0; x < w * 3 / 4; x += 7)
            {
                int i = y * stride + x * 4;
                if (pixels[i + 2] > 150 && pixels[i + 1] > 150 && pixels[i] < 60)
                    return true;
            }
        }
        return false;
    }
}
