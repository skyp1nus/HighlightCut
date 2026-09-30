using HighlightCut.Core.Model;
using HighlightCut.Core.Serialization;

namespace HighlightCut.Core.Tests.Serialization;

/// <summary>Version 2 project files (several videos) and version 1 files (one video) read as projects with one.</summary>
public class SeveralVideosFileTests
{
    private static Project Mixed() => Videos.Three
        .WithClips([
            new Clip(1, "A", 10, 20, Color: ClipColor.Teal, SourceId: 1),
            new Clip(4, "C", 0, 5, IsIncluded: false, Color: ClipColor.Rose, SourceId: 3),
            new Clip(2, "B", 10, 20, Color: ClipColor.Amber, SourceId: 2),
        ])
        .WithMix(new TrackMix(1, -6, SourceId: 1))
        .WithMix(new TrackMix(2, IsMuted: true, SourceId: 2)) with { LastSourceId = 5 };

    /// <summary>A video's fields: records holding an <c>ImmutableArray</c> compare it by reference.</summary>
    private static string Shape(SourceMedia s) =>
        $"{s.Id} {s.Path} {s.Duration} {s.FrameRate} [{string.Join(", ", s.AudioTracks)}]";

    [Fact]
    public void Several_videos_round_trip_with_their_clips_and_mix()
    {
        var project = Mixed();
        string json = ProjectFile.Serialize(project);
        Assert.Contains("\"version\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"sources\": [", json, StringComparison.Ordinal);
        Assert.Contains("\"source\": 3", json, StringComparison.Ordinal);
        Assert.Contains("\"lastSourceId\": 5", json, StringComparison.Ordinal);

        var back = ProjectFile.Deserialize(json);
        Assert.Equal(project.Sources.Select(Shape), back.Sources.Select(Shape));
        Assert.Equal(project.Clips, back.Clips);
        Assert.Equal(project.AudioMix, back.AudioMix);
        Assert.Equal(6, back.NextSourceId);
        Assert.Equal(5, back.NextClipId);
        Assert.Equal(new TimeRange(150, 155), back.TimelineRange(back.Get(4)));
    }

    [Fact]
    public void A_one_video_project_is_saved_as_version_2_and_reads_back_the_same()
    {
        string json = ProjectFile.Serialize(Sample.Project);
        Assert.Contains("\"sources\": [", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"source\": {", json, StringComparison.Ordinal);
        var back = ProjectFile.Deserialize(json);
        Assert.Equal(Sample.Project.Sources.Select(Shape), back.Sources.Select(Shape));
        Assert.Equal(Sample.Project.Clips, back.Clips);
    }

    [Fact]
    public void A_version_1_file_opens_as_a_project_with_one_video()
    {
        const string json = """
            {
              "format": "highlightcut-project", "version": 1, "name": "launch",
              "source": { "path": "/v/a.mp4", "duration": 872.48, "frameRate": 29.97,
                          "audioStreams": [ { "index": 1, "label": "Mic" }, { "index": 2, "label": "Game", "gainDb": -6, "muted": true } ] },
              "lastClipId": 7,
              "clips": [ { "id": 1, "label": "Intro", "start": 12.04, "end": 45.32, "included": true, "color": "teal" } ]
            }
            """;
        var p = ProjectFile.Deserialize(json);

        var source = Assert.Single(p.Sources);
        Assert.Equal(Shape(new SourceMedia("/v/a.mp4", 872.48, 29.97, [new AudioTrack(1, "Mic"), new AudioTrack(2, "Game")], SourceMedia.FirstId)),
            Shape(source));
        Assert.Equal(new Clip(1, "Intro", 12.04, 45.32, Color: ClipColor.Teal, SourceId: 1), Assert.Single(p.Clips));
        Assert.Equal([new TrackMix(2, -6, true, SourceId: 1)], p.AudioMix);
        Assert.Equal(2, p.NextSourceId);
        Assert.Equal(8, p.NextClipId);

        // Saved again, it is a version 2 file with the same project in it.
        var again = ProjectFile.Deserialize(ProjectFile.Serialize(p));
        Assert.Equal(p.Sources.Select(Shape), again.Sources.Select(Shape));
        Assert.Equal(p.Clips, again.Clips);
        Assert.Equal(p.AudioMix, again.AudioMix);
    }

    [Fact]
    public void An_ourcut_file_still_opens_as_one_video()
    {
        const string json = """
            { "format": "ourcut-project", "version": 1, "source": { "path": "/v/a.mp4", "duration": 10, "frameRate": 30 },
              "clips": [ { "id": 1, "start": 1, "end": 2 } ] }
            """;
        var p = ProjectFile.Deserialize(json);
        Assert.Equal(1, p.Source!.Id);
        Assert.Equal(1, p.Clips[0].SourceId);
    }

    [Fact]
    public void Each_video_path_is_relative_to_the_project_file_when_it_can_be()
    {
        string dir = Directory.CreateTempSubdirectory("highlightcut").FullName;
        try
        {
            string projectPath = Path.Combine(dir, "project", "trip.highlightcut.json");
            string inside = Path.Combine(dir, "project", "media", "a.mp4");
            string beside = Path.Combine(dir, "other", "b.mp4");
            var project = Videos.Two with { Sources = [Videos.A with { Path = inside }, Videos.B with { Path = beside }] };

            string json = ProjectFile.Serialize(project, projectPath);
            Assert.Contains("\"path\": \"media/a.mp4\"", json, StringComparison.Ordinal);
            Assert.Contains("\"path\": \"../other/b.mp4\"", json, StringComparison.Ordinal);
            Assert.Equal([inside, beside], ProjectFile.Deserialize(json, projectPath).Sources.Select(s => s.Path));

            // Without a project path (not saved yet) every path stays absolute.
            Assert.Equal([inside, beside], ProjectFile.Deserialize(ProjectFile.Serialize(project)).Sources.Select(s => s.Path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Relative_and_absolute_paths_can_be_mixed_in_one_file()
    {
        string dir = Directory.CreateTempSubdirectory("highlightcut").FullName;
        try
        {
            string projectPath = Path.Combine(dir, "trip.highlightcut.json");
            string absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "b.mp4");
            string json = $$"""
                {
                  "format": "highlightcut-project", "version": 2,
                  "sources": [
                    { "id": 1, "path": "media/a.mp4", "duration": 100, "frameRate": 30 },
                    { "id": 2, "path": "{{absolute.Replace("\\", "\\\\", StringComparison.Ordinal)}}", "duration": 50, "frameRate": 25 }
                  ],
                  "clips": [ { "id": 1, "source": 2, "start": 1, "end": 2 } ]
                }
                """;
            var p = ProjectFile.Deserialize(json, projectPath);
            Assert.Equal([Path.Combine(dir, "media", "a.mp4"), absolute], p.Sources.Select(s => s.Path));
            Assert.Equal(2, p.Clips[0].SourceId);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_video_on_another_drive_keeps_its_absolute_path()
    {
        if (!OperatingSystem.IsWindows())
            return;
        string projectPath = @"C:\projects\trip.highlightcut.json";
        var project = Videos.Two with { Sources = [Videos.A with { Path = @"C:\projects\a.mp4" }, Videos.B with { Path = @"Z:\camera\b.mp4" }] };
        string json = ProjectFile.Serialize(project, projectPath);
        Assert.Contains("\"path\": \"a.mp4\"", json, StringComparison.Ordinal);
        Assert.Contains(@"""path"": ""Z:\\camera\\b.mp4""", json, StringComparison.Ordinal);
        Assert.Equal([@"C:\projects\a.mp4", @"Z:\camera\b.mp4"], ProjectFile.Deserialize(json, projectPath).Sources.Select(s => s.Path));
    }

    [Fact]
    public void A_clip_may_leave_out_its_video_while_there_is_one()
    {
        const string json = """
            { "format": "highlightcut-project", "version": 2,
              "sources": [ { "id": 4, "path": "/v/a.mp4", "duration": 10, "frameRate": 30 } ],
              "clips": [ { "id": 1, "start": 1, "end": 2 } ] }
            """;
        Assert.Equal(4, ProjectFile.Deserialize(json).Clips[0].SourceId);
    }

    [Theory]
    [InlineData("""{ "id": 1, "path": "/v/a.mp4", "duration": 10 }, { "id": 1, "path": "/v/b.mp4", "duration": 5 }""", """{ "id": 1, "source": 1, "start": 1, "end": 2 }""", "Video id 1 appears twice.")]
    [InlineData("""{ "path": "/v/a.mp4", "duration": 10 }""", "", "a.mp4 has no valid id.")]
    [InlineData("""{ "id": 1, "path": "/v/a.mp4", "duration": 10 }""", """{ "id": 1, "source": 2, "start": 1, "end": 2 }""", "Clip 1 is in video 2, which the project does not have.")]
    [InlineData("""{ "id": 1, "path": "/v/a.mp4", "duration": 10 }, { "id": 2, "path": "/v/b.mp4", "duration": 5 }""", """{ "id": 1, "start": 1, "end": 2 }""", "Clip 1 does not say which video it is in.")]
    [InlineData("""{ "id": 1, "path": "", "duration": 10 }""", "", "A video of the project has no path.")]
    public void Invalid_videos_are_rejected_with_a_readable_error(string sources, string clips, string message)
    {
        string json = $$"""{ "format": "highlightcut-project", "version": 2, "sources": [ {{sources}} ], "clips": [ {{clips}} ] }""";
        Assert.Equal(message, Assert.Throws<ProjectFileException>(() => ProjectFile.Deserialize(json)).Message);
    }

    [Fact]
    public void A_newer_version_is_still_rejected() =>
        Assert.Throws<ProjectFileException>(() => ProjectFile.Deserialize("""{ "format": "highlightcut-project", "version": 3 }"""));
}
