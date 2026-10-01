using System.Globalization;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HighlightCut.App.Services;
using HighlightCut.App.ViewModels;
using HighlightCut.App.Views;
using HighlightCut.Core.Model;
using HighlightCut.Media;
using HighlightCut.Media.Caching;
using HighlightCut.Media.Probing;
using HighlightCut.Media.Tools;

namespace HighlightCut.App.Tests;

/// <summary>
/// Exporting a project with several real videos from the editor and through Claude's tools: part1 and part2 are alike
/// (320×180, 30 fps, two audio tracks), other is 240×240 at 25 fps with one. Each is 4 s with a keyframe every second.
/// </summary>
public sealed class RealMediaSeveralVideosTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-several").FullName;

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

    private async Task<string> VideoAsync(string name, string size = "320x180", int rate = 30, int tracks = 2)
    {
        Assert.SkipWhen(NativeTools.FindTool("ffmpeg") is null || NativeTools.FindTool("ffprobe") is null,
            "ffmpeg/ffprobe are not installed.");
        string path = Path.Combine(_dir, name);
        var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate={rate}" };
        for (int t = 0; t < tracks; t++)
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency={440 * (t + 1)}:sample_rate=48000"]);
        args.AddRange(["-t", "4", "-map", "0:v"]);
        for (int t = 0; t < tracks; t++)
            args.AddRange(["-map", $"{t + 1}:a"]);
        args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-g", rate.ToString(CultureInfo.InvariantCulture), "-keyint_min",
            rate.ToString(CultureInfo.InvariantCulture), "-sc_threshold", "0", "-bf", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", path]);
        await Task.Run(() => ToolProcess.RunAsync("ffmpeg", args, null, Ct), Ct);
        return path;
    }

    /// <summary>Opens the first video and adds the others, the Keyframes chip off (a lossless export finds them itself).</summary>
    private async Task<(EditorViewModel Editor, MainWindow Window)> OpenAsync(params string[] videos)
    {
        var editor = App.CreateEditor(null, new FfmpegMediaOpener(new MediaCache(Path.Combine(_dir, "cache"))),
            new RecentFilesStore(Path.Combine(_dir, "recent.json")));
        editor.Settings.ModelsFolder = Directory.CreateDirectory(Path.Combine(_dir, "no-models")).FullName;
        editor.ShowWaveform = false;
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        await editor.OpenMediaAsync(videos[0]);
        Assert.True(editor.HasFile, editor.StatusMessage);
        if (videos.Length > 1)
            Assert.Equal(videos.Length - 1, (await editor.AddVideosAsync(videos[1..])).Count);
        Dispatcher.UIThread.RunJobs();
        return (editor, window);
    }

    /// <summary>A clip from 1.5 s to 3 s into each video (the in-point half a second after a keyframe).</summary>
    private static void KeepOnePerVideo(EditorViewModel editor)
    {
        foreach (var source in editor.Session.Project.Sources)
        {
            double offset = editor.Session.Project.OffsetOf(source.Id);
            editor.KeepRange(offset + 1.5, offset + 3);
        }
        Assert.Equal(editor.Session.Project.Sources.Count, editor.Clips.Count);
    }

    private static async Task PumpUntil(Func<bool> done, int tries = 400)
    {
        for (int i = 0; i < tries && !done(); i++)
        {
            await Task.Delay(25, Ct);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(done());
    }

    [AvaloniaFact]
    public async Task The_dialog_says_why_videos_that_differ_cannot_be_joined_losslessly_and_re_encodes_them()
    {
        string part1 = await VideoAsync("part1.mp4"), part2 = await VideoAsync("part2.mp4"), other = await VideoAsync("other.mp4", "240x240", 25, 1);
        var (editor, window) = await OpenAsync(part1, part2, other);
        KeepOnePerVideo(editor);
        string outDir = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;
        var export = editor.Export;

        export.Open();
        export.OutputFolder = outDir;
        Assert.EndsWith(" · from 3 videos", export.Summary, StringComparison.Ordinal);
        Assert.True(export.IsCopy);
        Assert.Equal("Lossless can’t join these videos into one file: next to part1.mp4, other.mp4 has 240×240 instead of 320×180, " +
                     "25 fps instead of 30 and no audio track 2. Re-encode to join them, or export separate files.", export.LosslessProblem);
        Assert.False(export.ShowSnapNote);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using (var frame = window.CaptureRenderedFrame())
            frame!.Save(Path.Combine(Screenshots.Directory, "export-videos-differ.png"), new PngBitmapEncoderOptions());
        // Enter does not get past it.
        await export.StartAsync();
        Assert.True(export.IsConfiguring);
        Assert.Empty(Directory.GetFiles(outDir));
        // Separate files can be lossless.
        export.Merge = false;
        Assert.Null(export.LosslessProblem);
        export.Merge = true;

        export.UseReencodeCommand.Execute(null);
        Assert.True(export.IsEncode);
        Assert.Null(export.LosslessProblem);
        export.Video = HighlightCut.Media.Export.VideoEncoding.H264Fast;
        await export.StartAsync();

        Assert.True(export.IsDone, export.ErrorText);
        string output = Path.Combine(outDir, editor.ProjectName + "-cut.mp4");
        Assert.Equal([output], Directory.GetFiles(outDir));
        var result = await MediaProbe.ProbeAsync(output, Ct);
        Assert.Equal(4.5, result.Duration, 0.1);
        Assert.Equal((320, 180, "30"), (result.Video!.Width, result.Video.Height, result.Video.FrameRateText));
        Assert.Equal(2, result.Audio.Length);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Videos_alike_are_joined_losslessly_each_clip_on_its_own_videos_keyframes()
    {
        string part1 = await VideoAsync("part1.mp4"), part2 = await VideoAsync("part2.mp4");
        var (editor, window) = await OpenAsync(part1, part2);
        KeepOnePerVideo(editor);
        // Part 2's first track 12 dB down: only its pieces of that track change.
        editor.Session.SetTrackMix(new TrackMix(editor.Session.Project.Sources[1].AudioTracks[0].Index, -12, SourceId: 2));
        string outDir = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;
        var export = editor.Export;

        export.Open();
        export.OutputFolder = outDir;
        Assert.Null(export.LosslessProblem);
        Assert.True(export.ShowSnapNote);
        await export.StartAsync();

        Assert.True(export.IsDone, export.ErrorText);
        Assert.Equal(["1 · Clip 1", "2 · Clip 2", "Merge into " + editor.ProjectName + "-cut.mp4"], export.Rows.Select(r => r.Name));
        string output = Assert.Single(Directory.GetFiles(outDir));
        var result = await MediaProbe.ProbeAsync(output, Ct);
        // Both clips start back on the keyframe at 1 s of their own video: 2 s each.
        Assert.InRange(result.Duration, 3.95, 4.3);
        Assert.Equal(["aac", "aac"], result.Audio.Select(a => a.Codec));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Claude_adds_a_video_and_exports_the_videos_together()
    {
        string part1 = await VideoAsync("part1.mp4"), other = await VideoAsync("other.mp4", "240x240", 25, 1);
        var (editor, window) = await OpenAsync(part1);
        var tools = new HighlightCut.Mcp.EditorTools(new EditorMcpHost(editor));
        editor.Settings.ExportPermission = McpPermission.Allow;

        var added = await tools.AddVideo(other);

        Assert.Equal("Added video 2 (other.mp4)", added.Result);
        Assert.Equal([0, 4], added.Sources.Select(s => Math.Round(s.Offset, 1)));
        Assert.True(editor.HasSeveralVideos);
        Assert.IsType<TimelinePreview>(editor.Media);
        Assert.Equal(2, editor.Videos.Count);
        Assert.Contains(editor.Claude.Log, l => l.Text.Contains("other.mp4", StringComparison.Ordinal));
        Assert.Equal(HighlightCut.Core.Editing.EditOrigin.Assistant, Assert.Single(editor.Session.History.Entries).Origin);
        KeepOnePerVideo(editor);
        string outDir = Path.Combine(_dir, "by-claude");

        var refused = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tools.Export(mode: "lossless", folder: outDir));
        Assert.Contains("other.mp4 has 240×240 instead of 320×180", refused.Message, StringComparison.Ordinal);
        Assert.Contains("mode reencode", refused.Message, StringComparison.Ordinal);
        Assert.False(editor.Export.IsDialogOpen);

        var done = await tools.Export(mode: "reencode", video: "h264_fast", folder: outDir);
        await PumpUntil(() => editor.Export.Outcome != ExportOutcome.Running);
        Assert.Equal(ExportOutcome.Done, editor.Export.Outcome);
        Assert.Equal("Re-encode · MP4 · merged", done.Settings);
        var result = await MediaProbe.ProbeAsync(Assert.Single(editor.Export.OutputFiles), Ct);
        Assert.Equal(3, result.Duration, 0.1);
        Assert.Equal((320, 180), (result.Video!.Width, result.Video.Height));

        // Claude's remove_video and move_video edit the same timeline, each undoable.
        var moved = await tools.MoveVideo(2, 1);
        Assert.Equal(["other.mp4", "part1.mp4"], moved.Sources.Select(s => s.Name));
        var removed = await tools.RemoveVideo(1);
        Assert.Equal("Removed video 2 (part1.mp4) and its 1 clip", removed.Result);
        Assert.False(editor.HasSeveralVideos);
        editor.Undo();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, editor.Videos.Count);
        window.Close();
    }
}
