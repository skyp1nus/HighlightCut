using System.Runtime.InteropServices;
using OurCut.Media.Playback;
using OurCut.Media.Tests.Integration;

namespace OurCut.Media.Tests.Playback;

public class AudioMixTests
{
    [Fact]
    public void One_track_with_a_volume_goes_through_a_volume_filter() =>
        Assert.Equal(new AudioMix("auto", "[aid2]volume=-6dB[ao]"), AudioMix.For([false, true], [0, -6]));

    [Fact]
    public void Tracks_with_a_volume_are_filtered_before_the_mix()
    {
        Assert.Equal(new AudioMix("auto", "[aid1]volume=3.5dB[g1];[g1][aid2]amix=inputs=2:normalize=0[ao]"),
            AudioMix.For([true, true], [3.5, 0]));
        Assert.Equal(new AudioMix("auto", "[aid3]volume=0[g3];[aid1][g3]amix=inputs=2:normalize=0[ao]"),
            AudioMix.For([true, false, true], [0, 12, -40]));
    }

    [Fact]
    public void Muted_tracks_ignore_their_volume() =>
        Assert.Equal(new AudioMix("1", ""), AudioMix.For([true, false], [0, -12]));

    [Fact]
    public void One_enabled_track_is_played_directly() =>
        Assert.Equal(new AudioMix("2", ""), AudioMix.For([false, true, false]));

    [Fact]
    public void Several_tracks_are_mixed() =>
        Assert.Equal(new AudioMix("auto", "[aid1][aid3]amix=inputs=2:normalize=0[ao]"), AudioMix.For([true, false, true]));

    [Fact]
    public void All_muted_plays_no_audio() =>
        Assert.Equal(new AudioMix("no", ""), AudioMix.For([false, false]));

    [Fact]
    public void A_file_without_audio_has_nothing_to_play() =>
        Assert.Equal(new AudioMix("no", ""), AudioMix.For([]));
}

/// <summary>Plays generated media with libmpv (audio discarded). Skipped without libmpv or ffmpeg.</summary>
public sealed class MpvPlayerTests(SampleMediaFixture media) : IClassFixture<SampleMediaFixture>, IDisposable
{
    private MpvPlayer? _player;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _player?.Dispose();

    private async Task<MpvPlayer> LoadAsync(string? path = null)
    {
        media.SkipIfUnavailable();
        Assert.SkipUnless(MpvPlayer.IsAvailable(out string? error), error ?? "");
        _player = new MpvPlayer(new MpvPlayerOptions { AudioOutput = "null", HardwareDecoding = "no" });
        await _player.LoadAsync(path ?? media.Mp4).WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await WaitUntil(() => _player.Duration > 0);
        return _player;
    }

    private static async Task WaitUntil(Func<bool> condition, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for mpv.");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task Loading_reports_the_file_paused_at_the_start()
    {
        var player = await LoadAsync();
        Assert.Equal(10, player.Duration, 1);
        Assert.Equal(media.Mp4, player.LoadedPath);
        Assert.False(player.IsPlaying);
        Assert.Equal(0, player.Position, 2);
        await WaitUntil(() => player.VideoSize.Width > 0);
        Assert.Equal((320, 180), player.VideoSize);
    }

    [Fact]
    public async Task Seeks_are_frame_exact()
    {
        var player = await LoadAsync();
        player.Seek(4.5);
        // While the seek is in flight the position is its target; on a small file mpv may already have landed there
        // (SeekStateTests cover IsSeeking itself).
        Assert.Equal(4.5, player.Position, 3);
        await WaitUntil(() => !player.IsSeeking);
        Assert.Equal(4.5, player.Position, 3);

        // Rapid seeks while dragging: only the last one settles.
        player.Seek(1.0);
        player.Seek(2.0);
        player.Seek(7.2);
        await WaitUntil(() => !player.IsSeeking);
        Assert.Equal(7.2, player.Position, 2);
    }

    [Fact]
    public async Task Frame_steps_move_one_frame()
    {
        var player = await LoadAsync();
        player.Seek(3.0);
        await WaitUntil(() => !player.IsSeeking);

        player.StepFrame(forward: true);
        await WaitUntil(() => player.Position > 3.01);
        Assert.Equal(3 + 1 / 30.0, player.Position, 3);

        player.StepFrame(forward: false);
        await WaitUntil(() => player.Position < 3.01);
        Assert.Equal(3.0, player.Position, 3);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task Play_advances_and_pause_stops()
    {
        var player = await LoadAsync();
        int changes = 0;
        player.StateChanged += (_, _) => Interlocked.Increment(ref changes);

        player.Play();
        await WaitUntil(() => player.IsPlaying);
        await WaitUntil(() => player.Position > 0.3);
        player.Pause();
        await WaitUntil(() => !player.IsPlaying);
        double stopped = player.Position;
        await Task.Delay(200, Ct);

        Assert.Equal(stopped, player.Position, 3);
        Assert.True(changes > 3);
    }

    [Fact]
    public async Task Playback_stops_on_the_last_frame()
    {
        var player = await LoadAsync();
        player.SetSpeed(4);
        player.Seek(9.0);
        player.Play();
        await WaitUntil(() => player.IsEndReached);
        Assert.False(player.IsPlaying);
        Assert.InRange(player.Position, 9.8, 10.0);
    }

    [Fact]
    public async Task Muting_lanes_changes_the_audio_mix()
    {
        var player = await LoadAsync();
        player.SetAudioTracks([true, true]);
        await WaitUntil(() => player.GetPropertyString("lavfi-complex") == "[aid1][aid2]amix=inputs=2:normalize=0[ao]");

        player.SetAudioTracks([false, true]);
        await WaitUntil(() => player.GetPropertyString("lavfi-complex") == "" && player.GetPropertyString("aid") == "2");

        player.SetAudioTracks([false, false]);
        await WaitUntil(() => player.GetPropertyString("aid") == "no");
    }

    [Fact]
    public async Task Track_volumes_go_into_the_audio_mix()
    {
        var player = await LoadAsync();
        player.SetAudioTracks([true, true], [-6, 0]);
        await WaitUntil(() => player.GetPropertyString("lavfi-complex") == "[aid1]volume=-6dB[g1];[g1][aid2]amix=inputs=2:normalize=0[ao]");

        player.SetAudioTracks([false, true], [-6, 3]);
        await WaitUntil(() => player.GetPropertyString("lavfi-complex") == "[aid2]volume=3dB[ao]");
    }

    [Fact]
    public async Task Decoding_and_the_audio_device_change_while_playing()
    {
        var player = await LoadAsync();

        // The player was made with hwdec=no: mpv reports decoding on the CPU.
        await WaitUntil(() => player.CurrentDecoder == "no");

        player.SetHardwareDecoding("auto-copy");
        await WaitUntil(() => player.GetPropertyString("hwdec") == "auto-copy");
        player.SetHardwareDecoding("no");
        await WaitUntil(() => player.GetPropertyString("hwdec") == "no");

        // mpv lists devices of every audio output it has, even with ao=null; "auto" is the system default.
        var devices = player.AudioDevices();
        Assert.DoesNotContain(devices, d => d.Name == "auto");
        if (devices.Count > 0)
        {
            player.SetAudioDevice(devices[0].Name);
            await WaitUntil(() => player.GetPropertyString("audio-device") == devices[0].Name);
        }
        player.SetAudioDevice(null);
        await WaitUntil(() => player.GetPropertyString("audio-device") == "auto");
        Assert.Equal(media.Mp4, player.LoadedPath);
    }

    [Fact]
    public async Task Taking_the_renderer_away_while_playing_keeps_the_file_open()
    {
        var player = await LoadAsync();
        var renderer = new MpvSoftwareRenderer(player);
        var pixels = Marshal.AllocHGlobal(64 * 36 * 4);
        try
        {
            // The first switch reopens the file with the render context.
            await WaitUntil(() => player.GetPropertyString("current-vo") == "libmpv");
            player.Seek(1.5);
            player.Play();
            await WaitUntil(() =>
            {
                if (renderer.HasNewFrame())
                    renderer.Render(pixels, 64, 36, 64 * 4);
                return player.Position > 1.6;
            });
        }
        finally
        {
            Marshal.FreeHGlobal(pixels);
        }

        // mpv may end the file with an error when its video output goes; the player reopens it where it was.
        renderer.Dispose();

        await WaitUntil(() => player.GetPropertyString("path") == media.Mp4 && player.LoadedPath == media.Mp4 && player.Position > 1.5);
        Assert.NotEqual("libmpv", player.GetPropertyString("current-vo"));
    }

    [Fact]
    public void The_device_list_is_read_from_mpvs_json()
    {
        const string json = """
            [{"name":"auto","description":"Autoselect device"},{"name":"wasapi/{0.0.0}","description":"Speakers (Realtek)"},
             {"name":"pulse/hdmi"},{"description":"no name"}]
            """;

        Assert.Equal([new("wasapi/{0.0.0}", "Speakers (Realtek)"), new AudioOutputDevice("pulse/hdmi", "pulse/hdmi")],
            MpvPlayer.ParseAudioDevices(json));
        Assert.Empty(MpvPlayer.ParseAudioDevices(null));
        Assert.Empty(MpvPlayer.ParseAudioDevices("not json"));
        Assert.Empty(MpvPlayer.ParseAudioDevices("{}"));
    }

    [Fact]
    public async Task A_file_mpv_cannot_play_fails_to_load()
    {
        media.SkipIfUnavailable();
        Assert.SkipUnless(MpvPlayer.IsAvailable(out string? error), error ?? "");
        string path = Path.Combine(media.Folder, "not-a-video.mp4");
        await File.WriteAllTextAsync(path, "nope", Ct);
        _player = new MpvPlayer(new MpvPlayerOptions { AudioOutput = "null" });

        await Assert.ThrowsAsync<MpvException>(() => _player.LoadAsync(path).WaitAsync(TimeSpan.FromSeconds(20), Ct));
        Assert.Null(_player.LoadedPath);
    }

    [Fact]
    public async Task Attaching_a_renderer_while_a_file_loads_does_not_cancel_the_load()
    {
        media.SkipIfUnavailable();
        Assert.SkipUnless(MpvPlayer.IsAvailable(out string? error), error ?? "");
        _player = new MpvPlayer(new MpvPlayerOptions { AudioOutput = "null", HardwareDecoding = "no" });

        var load = _player.LoadAsync(media.Mp4);
        using var renderer = new MpvSoftwareRenderer(_player);
        await load.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        await WaitUntil(() => _player.LoadedPath == media.Mp4 && _player.Duration > 0);
        await WaitUntil(() => _player.GetPropertyString("current-vo") == "libmpv");
    }

    [Fact]
    public async Task The_software_renderer_draws_the_current_frame()
    {
        var player = await LoadAsync();
        player.Seek(2.0);
        await WaitUntil(() => !player.IsSeeking);

        // Attached after loading: the player reloads the file so video output can start.
        using var renderer = new MpvSoftwareRenderer(player);
        int updates = 0;
        renderer.UpdateRequested += (_, _) => Interlocked.Increment(ref updates);
        await WaitUntil(() => Volatile.Read(ref updates) > 0);
        await WaitUntil(() => !player.IsSeeking && player.LoadedPath is not null);

        const int w = 320, h = 180, stride = w * 4;
        IntPtr buffer = Marshal.AllocHGlobal(stride * h);
        try
        {
            var pixels = new byte[stride * h];
            await WaitUntil(() =>
            {
                renderer.HasNewFrame();
                renderer.Render(buffer, w, h, stride);
                Marshal.Copy(buffer, pixels, 0, pixels.Length);
                return pixels.Where((_, i) => i % 4 != 3).Average(b => b) > 40;
            });
            Assert.Equal(2.0, player.Position, 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

public class SeekStateTests
{
    [Fact]
    public void A_restart_left_over_from_before_a_seek_does_not_end_it()
    {
        var seeks = new SeekState();
        long first = seeks.Request(1);
        // The restart from loading the file is handled after the seek was asked for. (MpvPlayer used to keep a
        // separate flag that such a restart could clear while Seek set it; generations cannot be undone that way.)
        seeks.Restarted();
        Assert.True(seeks.IsSeeking);

        seeks.Replied(first, failed: false);
        Assert.True(seeks.IsSeeking);
        seeks.Restarted();
        Assert.False(seeks.IsSeeking);
    }

    [Fact]
    public void Only_the_newest_of_rapid_seeks_settles()
    {
        var seeks = new SeekState();
        long a = seeks.Request(1), b = seeks.Request(2), c = seeks.Request(3);
        seeks.Replied(a, false);
        seeks.Restarted();
        Assert.True(seeks.IsSeeking);
        seeks.Replied(c, false);
        seeks.Replied(b, false);
        seeks.Restarted();
        Assert.False(seeks.IsSeeking);
    }

    [Fact]
    public void A_failed_seek_settles_without_a_restart_unless_a_newer_one_is_pending()
    {
        var seeks = new SeekState();
        long a = seeks.Request(1);
        seeks.Replied(a, failed: true);
        Assert.False(seeks.IsSeeking);

        long b = seeks.Request(2), c = seeks.Request(3);
        seeks.Replied(b, failed: true);
        Assert.True(seeks.IsSeeking);
        seeks.Replied(c, failed: false);
        seeks.Restarted();
        Assert.False(seeks.IsSeeking);
    }

    [Fact]
    public void A_seek_that_lands_before_mpv_reports_the_new_position_keeps_the_target()
    {
        var seeks = new SeekState();
        seeks.Reported(2.0);
        long a = seeks.Request(6.2);
        // Playing: mpv reports the position it had before it got to the seek.
        seeks.Reported(2.04);
        Assert.Equal(6.2, seeks.Position);

        seeks.Replied(a, failed: false);
        seeks.Restarted();
        Assert.False(seeks.IsSeeking);
        Assert.Equal(6.2, seeks.Position);

        seeks.Reported(6.24);
        Assert.Equal(6.24, seeks.Position);
    }

    [Fact]
    public void A_restart_that_ends_no_seek_keeps_the_reported_position()
    {
        var seeks = new SeekState();
        long a = seeks.Request(2.0);
        seeks.Replied(a, failed: false);
        seeks.Restarted();
        seeks.Reported(5.0);

        // A new audio mix restarts playback too.
        seeks.Restarted();
        Assert.Equal(5.0, seeks.Position);
    }

    [Fact]
    public void A_failed_seek_keeps_the_reported_position()
    {
        var seeks = new SeekState();
        seeks.Reported(3.0);
        long a = seeks.Request(8.0);
        seeks.Replied(a, failed: true);
        seeks.Restarted();
        Assert.Equal(3.0, seeks.Position);
    }
}
