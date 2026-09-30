using HighlightCut.Core.Model;
using HighlightCut.Core.Serialization;

namespace HighlightCut.Core.Tests.Serialization;

public class ProjectFileTests
{
    [Fact]
    public void Round_trip_keeps_everything()
    {
        string json = ProjectFile.Serialize(Sample.Project);
        var back = ProjectFile.Deserialize(json);
        Assert.Equal(Sample.Project.Name, back.Name);
        Assert.Equal(Sample.Project.Clips, back.Clips);
        Assert.Equal(Sample.Project.Source!.Path, back.Source!.Path);
        Assert.Equal(Sample.Project.Source.AudioTracks, back.Source.AudioTracks);
        Assert.Equal(29.97, back.Source.FrameRate);
    }

    [Fact]
    public void File_format_is_readable_json_with_a_version()
    {
        string json = ProjectFile.Serialize(Sample.Project);
        Assert.Contains("\"format\": \"highlightcut-project\"", json, StringComparison.Ordinal);
        Assert.Contains("\"version\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"start\": 12.04", json, StringComparison.Ordinal);
        Assert.Contains("\"included\": false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_path_is_stored_relative_to_the_project_file()
    {
        string dir = Directory.CreateTempSubdirectory("highlightcut").FullName;
        try
        {
            string video = Path.Combine(dir, "media", "keynote.mp4");
            string projectPath = Path.Combine(dir, "keynote.highlightcut.json");
            var project = Sample.Project with { Sources = [Sample.Source with { Path = video }] };

            string json = ProjectFile.Serialize(project, projectPath);
            Assert.Contains("\"path\": \"media/keynote.mp4\"", json, StringComparison.Ordinal);
            Assert.Equal(video, ProjectFile.Deserialize(json, projectPath).Source!.Path);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Save_and_load_through_the_file_system()
    {
        string dir = Directory.CreateTempSubdirectory("highlightcut").FullName;
        try
        {
            string path = Path.Combine(dir, ProjectFile.FileNameFor(Sample.Project));
            await ProjectFile.SaveAsync(Sample.Project, path, TestContext.Current.CancellationToken);
            Assert.False(File.Exists(path + ".tmp"));
            var back = await ProjectFile.LoadAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(Sample.Project.Clips, back.Clips);
            Assert.Equal("launch-keynote", ProjectFile.NameFromPath(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Projects_saved_before_the_rename_still_open()
    {
        const string json = """
            { "format": "ourcut-project", "version": 1, "name": "Keynote", "clips": [ { "id": 1, "start": 1, "end": 2 } ] }
            """;
        var p = ProjectFile.Deserialize(json);
        Assert.Equal("Keynote", p.Name);
        Assert.Single(p.Clips);
        // Saved again, it gets the new format name.
        Assert.Contains("\"format\": \"highlightcut-project\"", ProjectFile.Serialize(p), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("launch.highlightcut.json", true, "launch")]
    [InlineData("launch.ourcut.json", true, "launch")]
    [InlineData("LAUNCH.OURCUT.JSON", true, "LAUNCH")]
    [InlineData("launch.json", false, "launch")]
    [InlineData("launch.mp4", false, "launch")]
    public void Both_extensions_are_project_files(string file, bool isProject, string name)
    {
        Assert.Equal(isProject, ProjectFile.IsProjectPath(file));
        Assert.Equal(name, ProjectFile.NameFromPath(file));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"format\":\"something-else\",\"version\":1}")]
    [InlineData("{\"format\":\"highlightcut-project\",\"version\":99}")]
    [InlineData("{\"format\":\"highlightcut-project\",\"version\":1,\"clips\":[{\"id\":1,\"start\":5,\"end\":2}]}")]
    [InlineData("{\"format\":\"highlightcut-project\",\"version\":1,\"clips\":[{\"id\":1,\"start\":1,\"end\":2},{\"id\":1,\"start\":3,\"end\":4}]}")]
    public void Invalid_files_are_rejected_with_a_readable_error(string json) =>
        Assert.Throws<ProjectFileException>(() => ProjectFile.Deserialize(json));

    [Fact]
    public void Track_volume_and_mute_are_saved_with_their_stream()
    {
        var project = Sample.Project.WithMix(new TrackMix(2, -6.5, IsMuted: true)).WithMix(new TrackMix(3, 4));
        string json = ProjectFile.Serialize(project);
        Assert.Contains("\"gainDb\": -6.5", json, StringComparison.Ordinal);
        Assert.Contains("\"muted\": true", json, StringComparison.Ordinal);
        // Tracks at the default (Mic) are written as before.
        Assert.Equal(2, json.Split("gainDb").Length - 1);
        Assert.Equal(1, json.Split("muted").Length - 1);

        var back = ProjectFile.Deserialize(json);
        Assert.Equal([new TrackMix(2, -6.5, true), new TrackMix(3, 4)], back.AudioMix);
        Assert.Equal(new TrackMix(1), back.MixOf(1, 1));
    }

    [Fact]
    public void Files_from_before_track_volumes_play_every_track_at_0_dB()
    {
        const string json = """
            {
              "format": "highlightcut-project", "version": 1,
              "source": { "path": "/v/a.mp4", "duration": 10, "frameRate": 30,
                          "audioStreams": [ { "index": 1, "label": "Mic" }, { "index": 2, "label": "Game" } ] },
              "clips": []
            }
            """;
        var p = ProjectFile.Deserialize(json);
        Assert.Empty(p.AudioMix);
        Assert.Equal(0, p.MixOf(1, 2).GainDb);
    }

    [Fact]
    public void Saved_volumes_are_kept_in_range()
    {
        const string json = """
            {
              "format": "highlightcut-project", "version": 1,
              "source": { "path": "/v/a.mp4", "duration": 10, "frameRate": 30,
                          "audioStreams": [ { "index": 1, "gainDb": 99 }, { "index": 2, "gainDb": "-Infinity" } ] }
            }
            """;
        var p = ProjectFile.Deserialize(json);
        Assert.Equal([new TrackMix(1, TrackMix.MaxGainDb), new TrackMix(2, TrackMix.MinGainDb)], p.AudioMix);
    }

    [Fact]
    public void Unknown_fields_and_missing_optional_ones_are_tolerated()
    {
        const string json = """
            {
              "format": "highlightcut-project", "version": 1, "futureField": {"x": 1},
              "clips": [ { "id": 3, "start": 1.5, "end": 4 } ]
            }
            """;
        var p = ProjectFile.Deserialize(json);
        Assert.Equal("Untitled project", p.Name);
        Assert.Null(p.Source);
        Assert.Equal(new Clip(3, "Clip 3", 1.5, 4, Color: ClipColor.Violet), Assert.Single(p.Clips));
    }

    [Fact]
    public void Numbers_are_written_with_an_invariant_decimal_point()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("uk-UA");
            Assert.Contains("\"duration\": 872.48", ProjectFile.Serialize(Sample.Project), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Theory]
    [InlineData("launch: keynote?", "launch_ keynote_.highlightcut.json")]
    [InlineData("   ", "project.highlightcut.json")]
    public void FileNameFor_replaces_characters_that_are_not_allowed(string name, string expected)
    {
        var p = Project.Empty with { Name = name };
        string file = ProjectFile.FileNameFor(p);
        if (OperatingSystem.IsWindows() || name.Trim().Length == 0)
            Assert.Equal(expected, file);
        else
            Assert.EndsWith(ProjectFile.Extension, file, StringComparison.Ordinal);
    }
}
