using HighlightCut.Media.Tools;

namespace HighlightCut.Media.Tests;

public class NativeToolsTests
{
    [Fact]
    public void FindTool_prefers_the_explicit_directory()
    {
        string dir = Directory.CreateTempSubdirectory("highlightcut-tools").FullName;
        try
        {
            string exe = Path.Combine(dir, NativeTools.ExecutableName("ffprobe"));
            File.WriteAllText(exe, "");
            Assert.Equal(exe, NativeTools.FindTool("ffprobe", dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FindTool_returns_null_for_unknown_tool() =>
        Assert.Null(NativeTools.FindTool("highlightcut-no-such-tool-" + Guid.NewGuid().ToString("N")));
}

public class NativeToolsVersionTests
{
    [Theory]
    [InlineData("ffmpeg version 7.1 Copyright (c) 2000-2024 the FFmpeg developers\nbuilt with gcc", "7.1")]
    [InlineData("ffmpeg version 6.1.1-3ubuntu5 Copyright (c) 2000-2023", "6.1.1")]
    [InlineData("ffmpeg version n9.0.1-11-ge47273f4d9-20260831 Copyright", "9.0.1")]
    [InlineData("ffprobe version 9.0.2-essentials_build-www.gyan.dev Copyright", "9.0.2")]
    [InlineData("ffmpeg version N-121000-g1234abcd Copyright", "N-121000-g1234abcd")]
    public void ParseVersion_reads_the_first_line(string output, string expected) =>
        Assert.Equal(expected, NativeTools.ParseVersion(output));

    [Fact]
    public void ParseVersion_returns_null_for_other_output() =>
        Assert.Null(NativeTools.ParseVersion("command not found"));

    [Fact]
    public async Task GetVersionAsync_returns_null_for_a_missing_tool() =>
        Assert.Null(await NativeTools.GetVersionAsync("highlightcut-missing-" + Guid.NewGuid().ToString("N"),
            cancellationToken: TestContext.Current.CancellationToken));
}

public class MediaToolExceptionTests
{
    [Theory]
    [InlineData("Stream map '0:9' matches no streams.\nTo ignore this, add a trailing '?' to the map.\n" +
                "Failed to set value '0:9' for option 'map': Invalid argument\nError parsing options for output file x.mp4.\n" +
                "Error opening output files: Invalid argument", "Stream map '0:9' matches no streams.")]
    [InlineData("[in#0 @ 0x55dbed63adc0] Error opening input: No such file or directory\nError opening input file a.mp4.\n" +
                "Error opening input files: No such file or directory", "Error opening input: No such file or directory")]
    [InlineData("[vost#0:0 @ 0x5623f24c8340] Unknown encoder 'libx999'\n[vost#0:0 @ 0x5623f24c8340] Error selecting an encoder\n" +
                "Error opening output file x.mp4.\nError opening output files: Encoder not found", "Unknown encoder 'libx999'")]
    [InlineData("Conversion failed!", "Conversion failed!")]
    public void Message_is_the_line_that_explains_the_error(string stderr, string expected) =>
        Assert.Equal("ffmpeg failed: " + expected, new MediaToolException("ffmpeg", 1, stderr).Message);

    [Fact]
    public void Message_without_output_has_the_exit_code() =>
        Assert.Equal("ffprobe failed with exit code 3.", new MediaToolException("ffprobe", 3, " \n").Message);
}
