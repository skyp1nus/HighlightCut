using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using HighlightCut.App;
using HighlightCut.App.Controls;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;
using HighlightCut.Media.Caching;
using HighlightCut.Media.Tools;
using HighlightCut.Transcription;

namespace HighlightCut.MemoryBench;

/// <summary>
/// Runs a realistic editing session in the real editor (headless window, libmpv, ffmpeg, sherpa-onnx) and measures
/// the process after each step. See README.md.
/// </summary>
internal static class Program
{
    private static Options _options = null!;
    private static Monitor _monitor = null!;

    public static int Main(string[] args)
    {
        _options = Options.Parse(args);
        AppBuilder.Configure<App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithHighlightCutFonts()
            .SetupWithoutStarting();
        VideoView.PreferOpenGl = false;
        using var done = new CancellationTokenSource();
        Exception? failure = null;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await RunAsync().ConfigureAwait(true);
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                done.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        if (failure is not null)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }
        return 0;
    }

    private static async Task RunAsync()
    {
        string work = Directory.CreateTempSubdirectory("highlightcut-memory").FullName;
        // A video given that does not exist yet is made there and kept, for the next run.
        string video = await MakeVideoAsync(_options.Video ?? Path.Combine(work, "long.mp4"), _options.Minutes, "1920x1080", 2).ConfigureAwait(true);
        string second = await MakeVideoAsync(_options.Second ?? Path.Combine(work, "second.mp4"), 10, "1280x720", 1).ConfigureAwait(true);
        using var monitor = _monitor = new Monitor();
        // Rendering runs as it would on screen (60 frames a second), so Skia's memory is counted.
        var render = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => AvaloniaHeadlessPlatform.ForceRenderTimerTick());
        render.Start();

        monitor.Begin("Started, no file");
        var player = MpvPlaybackEngine.TryCreate(out string? playbackError, new() { AudioOutput = "null" });
        var editor = App.App.CreateEditor(null, new FfmpegMediaOpener(new MediaCache(Path.Combine(work, "cache"))), null, player, playbackError);
        editor.Settings.Load(AppSettings.Default);
        editor.Settings.ModelsFolder = _options.Models ?? Directory.CreateDirectory(Path.Combine(work, "no-models")).FullName;
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        await using var mcp = new EditorMcpServer(editor, "highlightcut-memory-" + Environment.ProcessId);
        mcp.Start();
        await Idle(3).ConfigureAwait(true);
        monitor.End(player is null ? "no playback: " + playbackError : null);

        // Each file's steps run in a method of their own, so nothing here keeps its preview once the editor lets go.
        var previews = new List<WeakReference>
        {
            await LongFileAsync(editor, player, video).ConfigureAwait(true),
            await SecondFileAsync(editor, second).ConfigureAwait(true),
            await ReopenAsync(editor, video).ConfigureAwait(true),
        };

        monitor.Begin("Close the project");
        editor.Unload();
        await Idle(2).ConfigureAwait(true);
        monitor.End();

        monitor.Begin($"Idle {_options.Idle} s, nothing open");
        await Idle(_options.Idle).ConfigureAwait(true);
        monitor.End();

        monitor.Begin("After a full collection");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Idle(1).ConfigureAwait(true);
        monitor.End($"{previews.Count(p => p.IsAlive)} of {previews.Count} closed files still in memory");

        if (_options.WaitForDump)
        {
            Console.Error.WriteLine($"Waiting 2 minutes for a dump: dotnet-dump collect -p {Environment.ProcessId}");
            await Idle(120).ConfigureAwait(true);
        }
        render.Stop();
        window.Close();
        player?.Dispose();
        Report(video);
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The long video, with the default chips, then every chip and a transcript.</summary>
    private static async Task<WeakReference> LongFileAsync(EditorViewModel editor, MpvPlaybackEngine? player, string video)
    {
        var monitor = _monitor;
        monitor.Begin("Open a long video (Waveform chip)");
        await editor.OpenMediaAsync(video).ConfigureAwait(true);
        var media = (MediaPreview)editor.Media!;
        await media.Analysis.ConfigureAwait(true);
        await media.WaveformTask.ConfigureAwait(true);
        monitor.End($"{media.Duration / 60:0} min");

        monitor.Begin("Play 30 s");
        editor.TogglePlay();
        await Idle(30).ConfigureAwait(true);
        editor.TogglePlay();
        monitor.End(DemuxerCache(player));

        if (player is not null)
        {
            monitor.Begin("Scrub: 40 seeks");
            var random = new Random(1);
            var seeks = new List<double>();
            for (int i = 0; i < 40; i++)
            {
                double target = random.NextDouble() * media.Duration * 0.95;
                var watch = Stopwatch.StartNew();
                editor.SetTime(target);
                await WaitFor(() => !player.IsSeeking && Math.Abs(player.Position - target) < 0.1, 10).ConfigureAwait(true);
                seeks.Add(watch.Elapsed.TotalMilliseconds);
            }
            monitor.End(string.Create(CultureInfo.InvariantCulture, $"seek median {Median(seeks):0} ms, slowest {seeks.Max():0} ms, ")
                + DemuxerCache(player));
        }

        monitor.Begin("Keyframes chip on");
        editor.ShowKeyframes = true;
        await media.KeyframesTask.ConfigureAwait(true);
        await Idle(1).ConfigureAwait(true);
        monitor.End($"{media.Keyframes.Count} keyframes");

        monitor.Begin("Frames chip on");
        editor.ShowFrames = true;
        await media.ThumbnailsTask.ConfigureAwait(true);
        await Idle(1).ConfigureAwait(true);
        monitor.End($"{media.ThumbnailCount} thumbnails");

        monitor.Begin("Silence chip on");
        editor.ShowSilences = true;
        await Idle(1).ConfigureAwait(true);
        monitor.End($"{media.Silences.Count} silences");

        if (!_options.SkipScenes)
        {
            monitor.Begin("Scenes chip on");
            editor.ShowScenes = true;
            await media.ScenesTask.ConfigureAwait(true);
            await Idle(1).ConfigureAwait(true);
            monitor.End();
        }

        if (editor.StartTranscription() is { } why)
        {
            Console.Error.WriteLine("No transcription: " + why);
        }
        else
        {
            monitor.Begin("Transcribe");
            await WaitFor(() => media.TranscriptState is TranscriptState.Done or TranscriptState.Failed, 3 * 3600).ConfigureAwait(true);
            monitor.End(media.TranscriptState == TranscriptState.Failed ? "failed: " + media.TranscriptError
                : $"{editor.Settings.ActiveModel?.Id}, {media.Transcript?.Words.Count ?? 0} words");
        }

        monitor.Begin($"Idle {_options.Idle} s after the work");
        await Idle(_options.Idle).ConfigureAwait(true);
        monitor.End();
        return new WeakReference(media);
    }

    /// <summary>A second video, opened over the first with every chip still on.</summary>
    private static async Task<WeakReference> SecondFileAsync(EditorViewModel editor, string second)
    {
        _monitor.Begin("Open a second file");
        await editor.OpenMediaAsync(second).ConfigureAwait(true);
        var media = (MediaPreview)editor.Media!;
        await media.Analysis.ConfigureAwait(true);
        await Task.WhenAll(media.WaveformTask, media.ScenesTask).ConfigureAwait(true);
        await Idle(1).ConfigureAwait(true);
        _monitor.End($"{media.Duration / 60:0} min, every chip on");
        return new WeakReference(media);
    }

    /// <summary>The long video again, everything read from the cache (reopening a project).</summary>
    private static async Task<WeakReference> ReopenAsync(EditorViewModel editor, string video)
    {
        _monitor.Begin("Reopen the long video (from the cache)");
        await editor.OpenMediaAsync(video).ConfigureAwait(true);
        var media = (MediaPreview)editor.Media!;
        await media.Analysis.ConfigureAwait(true);
        await Task.WhenAll(media.WaveformTask, media.ScenesTask).ConfigureAwait(true);
        await Idle(1).ConfigureAwait(true);
        _monitor.End(media.AnalysisTimes);
        return new WeakReference(media);
    }

    /// <summary>What mpv's demuxer holds: "mpv demuxer cache 12 MB".</summary>
    private static string DemuxerCache(MpvPlaybackEngine? player)
    {
        if (player?.Mpv.GetPropertyString("demuxer-cache-state") is not { } json)
            return "no demuxer cache state";
        using var state = System.Text.Json.JsonDocument.Parse(json);
        long bytes = state.RootElement.TryGetProperty("total-bytes", out var total) ? total.GetInt64() : 0;
        return $"mpv demuxer cache {Monitor.Mb(bytes)} MB";
    }

    private static void Report(string video)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# HighlightCut memory, {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- {Environment.OSVersion}, {Environment.ProcessorCount} cores, .NET {Environment.Version}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- GC: {(GCSettings.IsServerGC ? "server" : "workstation")}, latency {GCSettings.LatencyMode}, "
            + $"concurrent {AppContext.GetData("System.GC.Concurrent") ?? "default"}, conserve memory {AppContext.GetData("System.GC.ConserveMemory") ?? "default"}, "
            + $"tiered compilation {AppContext.GetData("System.Runtime.TieredCompilation") ?? "default"}, tiered PGO {AppContext.GetData("System.Runtime.TieredPGO") ?? "default"}");
        var env = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("DOTNET_", StringComparison.Ordinal)
            && !k.StartsWith("DOTNET_CLI", StringComparison.Ordinal) && k != "DOTNET_NOLOGO" && k != "DOTNET_ROOT").Order().ToList();
        if (env.Count > 0)
            text.AppendLine("- Environment: " + string.Join(", ", env.Select(k => $"{k}={Environment.GetEnvironmentVariable(k)}")));
        text.AppendLine(CultureInfo.InvariantCulture, $"- Video: {Path.GetFileName(video)}, {new FileInfo(video).Length / 1024 / 1024} MB");
        text.AppendLine();
        text.AppendLine(_monitor.Table());
        text.AppendLine("Largest allocations over the whole session:");
        text.AppendLine();
        foreach (var (type, bytes, largest) in _monitor.AllAllocations(10))
            text.AppendLine(CultureInfo.InvariantCulture, $"- {type}: {Monitor.Mb(bytes)} MB (largest {largest / 1024} KB)");
        string report = text.ToString();
        Console.WriteLine(report);
        if (_options.Out is { } file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            File.WriteAllText(file, report);
        }
    }

    /// <summary>Keeps the UI running (rendering, timers, the player) for a while.</summary>
    private static Task Idle(double seconds) => Task.Delay(TimeSpan.FromSeconds(seconds));

    private static async Task WaitFor(Func<bool> done, double timeoutSeconds)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed.TotalSeconds > timeoutSeconds)
                throw new TimeoutException("Gave up waiting after " + timeoutSeconds + " s.");
            await Task.Delay(20).ConfigureAwait(true);
        }
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>A test picture with a tone per audio track; the first track goes quiet for 3 s every 20 s.</summary>
    private static async Task<string> MakeVideoAsync(string path, double minutes, string size, int tracks)
    {
        if (File.Exists(path))
            return path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Console.Error.WriteLine($"Making a {minutes:0}-minute {size} video with {tracks} audio track(s)…");
        var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=30" };
        for (int i = 0; i < tracks; i++)
            args.AddRange(["-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"sine=frequency={440 + 220 * i}:sample_rate=48000")]);
        args.AddRange(["-t", (minutes * 60).ToString(CultureInfo.InvariantCulture), "-map", "0:v"]);
        for (int i = 0; i < tracks; i++)
            args.AddRange(["-map", $"{i + 1}:a"]);
        args.AddRange(["-filter:a:0", "volume='if(lt(mod(t,20),3),0,1)':eval=frame", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "32",
            "-g", "60", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "96k", "-y", path]);
        await ToolProcess.RunAsync("ffmpeg", args, null, CancellationToken.None).ConfigureAwait(true);
        return path;
    }

    private sealed record Options(string? Video, string? Second, double Minutes, string? Models, double Idle, bool SkipScenes, string? Out,
        bool WaitForDump = false)
    {
        public static Options Parse(string[] args)
        {
            var o = new Options(null, null, 90, Environment.GetEnvironmentVariable("HIGHLIGHTCUT_MODELS_DIR") is { Length: > 0 } m ? m : null,
                30, false, null);
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => args[++i];
                o = args[i] switch
                {
                    "--video" => o with { Video = Next() },
                    "--second" => o with { Second = Next() },
                    "--minutes" => o with { Minutes = double.Parse(Next(), CultureInfo.InvariantCulture) },
                    "--models" => o with { Models = Next() },
                    "--idle" => o with { Idle = double.Parse(Next(), CultureInfo.InvariantCulture) },
                    "--no-scenes" => o with { SkipScenes = true },
                    "--out" => o with { Out = Next() },
                    "--wait-for-dump" => o with { WaitForDump = true },
                    _ => throw new ArgumentException("Unknown option " + args[i] + ". See scripts/memory/README.md."),
                };
            }
            return o;
        }
    }
}
