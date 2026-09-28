using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using HighlightCut.App.Controls;
using HighlightCut.App.Demo;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;
using HighlightCut.Media;
using HighlightCut.Media.Caching;
using HighlightCut.Media.Playback;
using HighlightCut.Transcription.Models;

namespace HighlightCut.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var demo = ParseDemoScreen(args);
            // Read before the player exists: it starts with the saved decoding and audio device.
            var store = demo is null ? new AppSettingsStore(AppSettingsStore.DefaultFile) : null;
            var saved = store?.Load() ?? AppSettings.Default;
            var playback = saved.Playback ?? new PlaybackSettings();
            var player = MpvPlaybackEngine.TryCreate(out string? playbackError,
                new MpvPlayerOptions { HardwareDecoding = playback.MpvHardwareDecoding(), AudioDevice = playback.AudioDevice });
            var editor = CreateEditor(demo, new FfmpegMediaOpener(new MediaCache(AppDataFolder.PathFor("cache"))),
                demo is null ? new RecentFilesStore(RecentFilesStore.DefaultFile) : null, player, playbackError);
            EditorMcpServer? mcp = null;
            if (demo is null)
            {
                editor.Settings.Load(saved);
                editor.Settings.Store = store;
                editor.Settings.Installer = new ModelInstaller(ModelInstaller.CreateHttpClient());
                editor.RevealInFolder = FileManager.Reveal;
                editor.Settings.ShowRenameNote = AppDataFolder.MovedFromLegacy;
                // A few test encodes in the background; the Export dialog uses what they find.
                _ = editor.Settings.DetectGpuEncoderAsync();
                // Claude connects through "HighlightCut mcp" (Settings → MCP server).
                mcp = new EditorMcpServer(editor);
                mcp.Start();
            }
            // The window (and with it the video view's renderer) closes first; then Claude's connection and the player core.
            desktop.Exit += (_, _) =>
            {
                mcp?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                player?.Dispose();
            };
            CrashLog.Install(editor.ShowMessage);
            var window = new MainWindow { DataContext = editor };
            editor.Dialogs = new StorageFileDialogs(window);
            editor.Settings.CopyText = text => window.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
            desktop.MainWindow = window;
            _ = editor.StartAsync(ParseFileArgument(args));
            // Once, on the first start after the rename: Claude Code still knows the app by its old name.
            if (demo is null && AppDataFolder.MovedAtStart is { Found: true })
                editor.ShowMessage("OurCut is now HighlightCut. To use it with Claude, add it again: Settings → MCP server.");
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Creates the editor. With a demo screen it shows the design's sample project in that state;
    /// otherwise it starts empty.
    /// </summary>
    /// <param name="opener">Opens videos; null when the editor never opens real files (tests).</param>
    /// <param name="recent">Recent files list; null keeps no history.</param>
    /// <param name="player">Video playback; null simulates it over thumbnails.</param>
    /// <param name="playbackError">Why there is no player, shown in the status bar.</param>
    public static EditorViewModel CreateEditor(DesignScreen? demo, IMediaOpener? opener = null, RecentFilesStore? recent = null,
        IPlayer? player = null, string? playbackError = null)
    {
        var editor = new EditorViewModel { MediaOpener = opener, RecentStore = recent, Player = player };
        if (demo is { } screen)
        {
            DemoScenario.Apply(editor, screen);
        }
        else
        {
            editor.LoadRecentFiles();
            _ = CheckToolsAsync(editor, playbackError);
        }
        return editor;
    }

    private static async Task CheckToolsAsync(EditorViewModel editor, string? playbackError)
    {
        string? version = await Task.Run(() => NativeTools.GetVersionAsync("ffmpeg")).ConfigureAwait(true);
        editor.ToolStatus = version is null
            ? "ffmpeg not found · run scripts/fetch-deps.ps1"
            : $"ffmpeg {version} · ready";
        if (playbackError is not null)
            editor.ToolStatus += " · no playback (libmpv unavailable)";
    }

    /// <summary>
    /// Reads <c>--demo &lt;screen&gt;</c> from the command line: a <see cref="DesignScreen"/> name in any case,
    /// or kebab-case (<c>claude-export-failed</c>).
    /// </summary>
    public static DesignScreen? ParseDemoScreen(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] != "--demo")
                continue;
            string value = i + 1 < args.Count ? args[i + 1].Replace("-", "", StringComparison.Ordinal) : "editing";
            return Enum.TryParse<DesignScreen>(value, ignoreCase: true, out var screen) && Enum.IsDefined(screen) ? screen : DesignScreen.Editing;
        }
        return null;
    }

    /// <summary>A video or project passed on the command line ("Open with HighlightCut").</summary>
    public static string? ParseFileArgument(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--demo")
            {
                i++;
                continue;
            }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) && File.Exists(args[i]))
                return args[i];
        }
        return null;
    }
}
