using HighlightCut.Core.Editing;
using HighlightCut.Core.Model;

namespace HighlightCut.Mcp.Tests;

/// <summary>
/// Claude's tools on a project with two videos end to end: keynote.mp4 (600 s, id 1) then qa.mp4 (300 s, id 2), so the
/// timeline runs keynote 0–600, qa 600–900. Claude gives and gets timeline seconds.
/// </summary>
public class McpSeveralVideosTests
{
    private static readonly SourceMedia Keynote = FakeEditor.Sample.Source!;
    private static readonly SourceMedia Qa = new("/videos/qa.mp4", 300, 25, [new AudioTrack(1, "Room")], 2);

    private static FakeEditor Editor(params Clip[] clips)
    {
        var editor = new FakeEditor();
        editor.Open(new Project("trip", [Keynote, Qa], [.. clips]).WithSources([Keynote, Qa]));
        return editor;
    }

    private static FakeEditor TwoClips() => Editor(new Clip(1, "Intro", 10, 40), new Clip(2, "Question", 20, 50, SourceId: 2));

    [Fact]
    public async Task Get_project_lists_the_videos_and_each_clips_timeline_range_and_video()
    {
        var editor = TwoClips();
        editor.Session.SetTrackMix(new TrackMix(1, -3, SourceId: 2));
        await using var c = await Connection.OpenAsync(editor);

        var p = (await c.Client.Call("get_project")).Json();

        Assert.Equal(900, p.GetProperty("timelineDuration").GetDouble());
        var sources = p.GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal([1, 2], sources.Select(s => s.GetProperty("id").GetInt32()));
        Assert.Equal(["keynote.mp4", "qa.mp4"], sources.Select(s => s.GetProperty("name").GetString()));
        Assert.Equal([0, 600], sources.Select(s => s.GetProperty("offset").GetDouble()));
        Assert.Equal([600, 300], sources.Select(s => s.GetProperty("duration").GetDouble()));
        Assert.Equal(-3, sources[1].GetProperty("audioTracks")[0].GetProperty("volumeDb").GetDouble());
        Assert.Equal(0, sources[0].GetProperty("audioTracks")[0].GetProperty("volumeDb").GetDouble());
        // The first video is still there as "source", for clients that read one video.
        Assert.Equal(1, p.GetProperty("source").GetProperty("id").GetInt32());

        var clips = p.GetProperty("clips").EnumerateArray().ToList();
        Assert.Equal([1, 2], clips.Select(x => x.GetProperty("source").GetInt32()));
        Assert.Equal([620, 650], [clips[1].GetProperty("start").GetDouble(), clips[1].GetProperty("end").GetDouble()]);
        Assert.Equal("10:20.000–10:50.000", clips[1].GetProperty("range").GetString());
        Assert.Equal(30, clips[1].GetProperty("duration").GetDouble());
    }

    [Fact]
    public async Task A_range_over_the_join_becomes_one_clip_per_video_in_one_step()
    {
        var editor = Editor();
        await using var c = await Connection.OpenAsync(editor);

        var r = (await c.Client.Call("add_segment", new { start = 590, end = 610, label = "Join" })).Json();

        Assert.Equal([(1, 590.0, 600.0), (2, 0.0, 10.0)], editor.Session.Project.Clips.Select(x => (x.SourceId, x.Start, x.End)));
        Assert.Equal(["Join", "Join · 2"], editor.Session.Project.Clips.Select(x => x.Label));
        Assert.Single(editor.Session.History.Entries);
        var clips = r.GetProperty("clips").EnumerateArray().ToList();
        Assert.Equal([600, 610], [clips[1].GetProperty("start").GetDouble(), clips[1].GetProperty("end").GetDouble()]);
    }

    [Fact]
    public async Task Trim_and_split_take_timeline_times_inside_the_clips_video()
    {
        var editor = TwoClips();
        await using var c = await Connection.OpenAsync(editor);

        (await c.Client.Call("trim_segment", new { clip = 2, start = 610 })).Json();
        Assert.Equal((10.0, 50.0), (editor.Session.Project.Get(2).Start, editor.Session.Project.Get(2).End));

        (await c.Client.Call("split_segment", new { clip = 2, time = 630 })).Json();
        Assert.Equal((2, 30.0, 50.0), editor.Session.Project.Clips.Where(x => x.Id == 3).Select(x => (x.SourceId, x.Start, x.End)).Single());

        var outside = await c.Client.Call("trim_segment", new { clip = 2, start = 590 });
        Assert.True(outside.IsError);
        Assert.Contains("Clip 2 is in video 2, which runs 10:00.000 – 15:00.000 on the timeline", outside.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clips_of_different_videos_cannot_be_joined()
    {
        var editor = Editor(new Clip(1, "End", 580, 600), new Clip(2, "Start", 0, 20, SourceId: 2));
        await using var c = await Connection.OpenAsync(editor);

        var join = await c.Client.Call("join_segments", new { clip = 1 });
        Assert.True(join.IsError);
        Assert.Contains("Clip 1 is the last clip of video 1; clips in different videos cannot be joined.", join.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cut_ranges_takes_timeline_ranges_and_cuts_both_sides_of_a_join()
    {
        var editor = Editor(new Clip(1, "End", 580, 600), new Clip(2, "Start", 0, 30, SourceId: 2));
        await using var c = await Connection.OpenAsync(editor);

        (await c.Client.Call("cut_ranges", new { ranges = new[] { new { start = 590, end = 610 } }, description = "Cut the join" })).Json();

        Assert.Equal([(1, 580.0, 590.0), (2, 10.0, 30.0)], editor.Session.Project.Clips.Select(x => (x.SourceId, x.Start, x.End)));
        Assert.Equal("Cut the join", Assert.Single(editor.Session.History.Entries).Description);
    }

    [Fact]
    public async Task Without_clips_a_cut_keeps_every_video_whole_first()
    {
        var editor = Editor();
        await using var c = await Connection.OpenAsync(editor);

        var r = (await c.Client.Call("cut_ranges", new { ranges = new[] { new { start = 700, end = 710 } }, description = "Cut a cough" })).Json();

        Assert.Equal([(1, 0.0, 600.0), (2, 0.0, 100.0), (2, 110.0, 300.0)], editor.Session.Project.Clips.Select(x => (x.SourceId, x.Start, x.End)));
        Assert.Equal("Cut a cough: 10 s shorter.", r.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Edit_timeline_maps_its_times_onto_the_videos()
    {
        var editor = TwoClips();
        await using var c = await Connection.OpenAsync(editor);

        (await c.Client.Call("edit_timeline", new
        {
            operations = new object[]
            {
                new { action = "add", start = 595, end = 605, label = "Join" },
                new { action = "split", clip = 2, time = 640 },
                new { action = "trim", clip = 1, end = 45 },
            },
            description = "Tidied up",
        })).Json();

        var p = editor.Session.Project;
        Assert.Equal([(1, 10.0, 45.0), (2, 20.0, 40.0), (2, 40.0, 50.0), (1, 595.0, 600.0), (2, 0.0, 5.0)],
            p.Clips.Select(x => (x.SourceId, x.Start, x.End)));
        Assert.Single(editor.Session.History.Entries);
    }

    [Fact]
    public async Task Seek_goes_to_a_clips_place_on_the_timeline()
    {
        var editor = TwoClips();
        await using var c = await Connection.OpenAsync(editor);

        (await c.Client.Call("seek", new { clip = 2 })).Text();
        Assert.Equal(620, editor.Playhead);
        (await c.Client.Call("seek", new { time = 5000 })).Text();
        Assert.Equal(900, editor.Playhead);
    }

    [Fact]
    public async Task With_one_video_the_times_are_the_videos_own()
    {
        await using var c = await Connection.OpenAsync(new FakeEditor());

        var p = (await c.Client.Call("get_project")).Json();

        Assert.Equal(600, p.GetProperty("timelineDuration").GetDouble());
        var source = Assert.Single(p.GetProperty("sources").EnumerateArray().ToList());
        Assert.Equal(0, source.GetProperty("offset").GetDouble());
        Assert.Equal("keynote.mp4 · 1080p · 30 fps", source.GetProperty("summary").GetString());
        var clip = p.GetProperty("clips")[1];
        Assert.Equal((100, 200, 1), (clip.GetProperty("start").GetDouble(), clip.GetProperty("end").GetDouble(), clip.GetProperty("source").GetInt32()));
    }

    // ---- add_video, remove_video, move_video ---------------------------------------------

    [Fact]
    public async Task Add_video_appends_a_video_as_one_undoable_edit()
    {
        string dir = Directory.CreateTempSubdirectory("highlightcut-mcp").FullName;
        try
        {
            string video = Path.Combine(dir, "outro.mp4");
            await File.WriteAllTextAsync(video, "", TestContext.Current.CancellationToken);
            var editor = TwoClips();
            await using var c = await Connection.OpenAsync(editor);

            var r = (await c.Client.Call("add_video", new { path = video })).Json();

            Assert.Equal([video], editor.Added);
            Assert.Equal(["keynote.mp4", "qa.mp4", "outro.mp4"], editor.Session.Project.Sources.Select(s => s.FileName));
            Assert.Equal("Added video 3 (outro.mp4)", r.GetProperty("result").GetString());
            var sources = r.GetProperty("sources").EnumerateArray().ToList();
            Assert.Equal((3, 900.0), (sources[2].GetProperty("id").GetInt32(), sources[2].GetProperty("offset").GetDouble()));
            Assert.Equal(1020, r.GetProperty("timelineDuration").GetDouble());
            var entry = Assert.Single(editor.Session.History.Entries);
            Assert.Equal(r.GetProperty("action").GetInt64(), entry.Id);
            Assert.Equal(EditOrigin.Assistant, entry.Origin);
            // The clips stay; the new video's are cut from 900 s on.
            Assert.Equal(2, r.GetProperty("clips").GetArrayLength());
            (await c.Client.Call("add_segment", new { start = 910, end = 920 })).Json();
            Assert.Equal((3, 10.0, 20.0), editor.Session.Project.Clips.Select(x => (x.SourceId, x.Start, x.End)).Last());

            (await c.Client.Call("undo")).Json();
            (await c.Client.Call("undo")).Json();
            Assert.Equal(2, editor.Session.Project.Sources.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Add_video_reports_why_a_video_was_not_added()
    {
        string dir = Directory.CreateTempSubdirectory("highlightcut-mcp").FullName;
        try
        {
            string broken = Path.Combine(dir, "bad.broken"), video = Path.Combine(dir, "outro.mp4");
            await File.WriteAllTextAsync(broken, "", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(video, "", TestContext.Current.CancellationToken);
            var editor = TwoClips();
            await using var c = await Connection.OpenAsync(editor);

            var failed = await c.Client.Call("add_video", new { path = broken });
            Assert.True(failed.IsError);
            Assert.Contains("Could not open bad.broken", failed.Text(), StringComparison.Ordinal);
            Assert.Contains("does not exist", (await c.Client.Call("add_video", new { path = Path.Combine(dir, "missing.mp4") })).Text(),
                StringComparison.Ordinal);
            Assert.Contains("full path", (await c.Client.Call("add_video", new { path = "outro.mp4" })).Text(), StringComparison.Ordinal);
            editor.AddRefusal = "The user declined adding outro.mp4.";
            Assert.Contains("The user declined adding outro.mp4.", (await c.Client.Call("add_video", new { path = video })).Text(), StringComparison.Ordinal);
            Assert.Equal(2, editor.Session.Project.Sources.Count);
            Assert.Empty(editor.Session.History.Entries);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Add_video_needs_an_open_project()
    {
        await using var c = await Connection.OpenAsync(new FakeEditor(withFile: false));

        var r = await c.Client.Call("add_video", new { path = Path.GetFullPath("outro.mp4") });

        Assert.True(r.IsError);
        Assert.Contains("open_file", r.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_video_takes_its_clips_with_it_and_says_how_many()
    {
        var editor = Editor(new Clip(1, "Intro", 10, 40), new Clip(2, "Question", 20, 50, SourceId: 2), new Clip(3, "Answer", 60, 90, SourceId: 2));
        await using var c = await Connection.OpenAsync(editor);

        var r = (await c.Client.Call("remove_video", new { video = 2 })).Json();

        Assert.Equal("Removed video 2 (qa.mp4) and its 2 clips", r.GetProperty("result").GetString());
        Assert.Equal([1], r.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("id").GetInt32()));
        Assert.Equal([1], editor.Session.Project.Clips.Select(x => x.Id));
        Assert.Equal(600, r.GetProperty("timelineDuration").GetDouble());

        (await c.Client.Call("undo")).Json();
        Assert.Equal(3, editor.Session.Project.Clips.Count);

        var missing = await c.Client.Call("remove_video", new { video = 7 });
        Assert.True(missing.IsError);
        Assert.Contains("There is no video 7; the videos are 1 (keynote.mp4), 2 (qa.mp4).", missing.Text(), StringComparison.Ordinal);
        (await c.Client.Call("remove_video", new { video = 2 })).Json();
        var only = await c.Client.Call("remove_video", new { video = 1 });
        Assert.True(only.IsError);
        Assert.Contains("only video", only.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Move_video_reorders_the_timeline_and_its_clips_go_along()
    {
        var editor = TwoClips();
        await using var c = await Connection.OpenAsync(editor);

        var r = (await c.Client.Call("move_video", new { video = 2, position = 1 })).Json();

        Assert.Equal("Moved video 2 to position 1", r.GetProperty("result").GetString());
        Assert.Equal([2, 1], editor.Session.Project.Sources.Select(s => s.Id));
        var sources = r.GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal([0, 300], sources.Select(s => s.GetProperty("offset").GetDouble()));
        // The output order stays; the clips' timeline ranges follow their videos.
        var clips = r.GetProperty("clips").EnumerateArray().ToList();
        Assert.Equal([1, 2], clips.Select(x => x.GetProperty("id").GetInt32()));
        Assert.Equal([310, 20], clips.Select(x => x.GetProperty("start").GetDouble()));
        Assert.Equal(EditOrigin.Assistant, Assert.Single(editor.Session.History.Entries).Origin);

        var outside = await c.Client.Call("move_video", new { video = 1, position = 3 });
        Assert.True(outside.IsError);
        Assert.Contains("Position 3 is outside the video list (1–2).", outside.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_instructions_tell_Claude_about_the_video_tools()
    {
        await using var c = await Connection.OpenAsync(Editor());

        Assert.Contains("add_video", c.Client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("remove_video", c.Client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("move_video", c.Client.ServerInstructions, StringComparison.Ordinal);
    }
}
