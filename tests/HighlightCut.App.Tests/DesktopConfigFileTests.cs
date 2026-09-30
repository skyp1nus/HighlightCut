using System.Text;
using System.Text.Json.Nodes;
using HighlightCut.App.Services;

namespace HighlightCut.App.Tests;

/// <summary>Putting HighlightCut into Claude Desktop's claude_desktop_config.json without losing anything else in it.</summary>
public sealed class DesktopConfigFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("highlightcut-desktop-config").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Exe = @"C:\Program Files\HighlightCut\HighlightCut.exe";

    private string File_ => Path.Combine(_dir, "Claude", "claude_desktop_config.json");

    private static JsonObject Servers(string json) => (JsonObject)JsonNode.Parse(json)!["mcpServers"]!;

    private static void AssertHighlightCut(JsonObject servers, string command = Exe)
    {
        var entry = (JsonObject)servers["highlightcut"]!;
        Assert.Equal(command, (string?)entry["command"]);
        Assert.Equal(["mcp"], ((JsonArray)entry["args"]!).Select(a => (string?)a));
    }

    [Fact]
    public void No_file_is_created_with_highlightcut()
    {
        var result = DesktopConfigFile.Add(File_, Exe, ["mcp"]);

        Assert.Equal(DesktopConfigOutcome.Created, result.Outcome);
        AssertHighlightCut(Servers(File.ReadAllText(File_)));
        Assert.False(File.Exists(File_ + ".bak"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(File_)!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    [InlineData("{}")]
    public void An_empty_file_gets_mcp_servers(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, text);

        var result = DesktopConfigFile.Add(File_, Exe, ["mcp"]);

        Assert.Equal(DesktopConfigOutcome.Updated, result.Outcome);
        AssertHighlightCut(Servers(File.ReadAllText(File_)));
        Assert.Equal(text, File.ReadAllText(File_ + ".bak"));
    }

    [Fact]
    public void Other_servers_and_keys_are_kept_in_their_order()
    {
        const string Text = """
            {
              "globalShortcut": "Ctrl+Space",
              "mcpServers": {
                "filesystem": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\a"], "env": { "A": "1" } },
                "github": { "command": "docker", "args": [] }
              },
              "preferences": { "theme": "dark" }
            }
            """;
        var result = DesktopConfigFile.Merge(Text, Exe, ["mcp"]);

        Assert.True(result.Changed);
        var root = (JsonObject)JsonNode.Parse(result.Json!)!;
        Assert.Equal(["globalShortcut", "mcpServers", "preferences"], root.Select(p => p.Key));
        Assert.Equal("Ctrl+Space", (string?)root["globalShortcut"]);
        Assert.Equal("dark", (string?)root["preferences"]!["theme"]);
        var servers = (JsonObject)root["mcpServers"]!;
        Assert.Equal(["filesystem", "github", "highlightcut"], servers.Select(p => p.Key));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Text)!["mcpServers"]!["filesystem"], servers["filesystem"]));
        AssertHighlightCut(servers);
    }

    [Fact]
    public void An_old_highlightcut_path_is_replaced_in_place()
    {
        const string Text = """
            { "mcpServers": {
                "a": { "command": "a" },
                "HighlightCut": { "command": "D:\\Old\\HighlightCut.exe", "args": ["mcp"] },
                "b": { "command": "b" } } }
            """;
        var result = DesktopConfigFile.Merge(Text, Exe, ["mcp"]);

        var servers = Servers(result.Json!);
        Assert.Equal(["a", "b", "highlightcut"], servers.Select(p => p.Key));
        AssertHighlightCut(servers);

        // Adding again changes nothing, and leaves the file alone.
        Assert.False(DesktopConfigFile.Merge(result.Json, Exe, ["mcp"]).Changed);
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, result.Json);
        var written = File.GetLastWriteTimeUtc(File_);
        Assert.Equal(DesktopConfigOutcome.AlreadyAdded, DesktopConfigFile.Add(File_, Exe, ["mcp"]).Outcome);
        Assert.False(File.Exists(File_ + ".bak"));
        Assert.Equal(written, File.GetLastWriteTimeUtc(File_));
    }

    [Fact]
    public void The_old_ourcut_entry_goes_but_another_ourcut_stays()
    {
        const string Old = """{ "mcpServers": { "ourcut": { "command": "C:\\Program Files\\OurCut\\OurCut.exe", "args": ["mcp"] }, "x": {} } }""";
        var result = DesktopConfigFile.Merge(Old, Exe, ["mcp"]);
        Assert.True(result.RemovedOurCut);
        Assert.Equal(["x", "highlightcut"], Servers(result.Json!).Select(p => p.Key));

        // Something else the user called "ourcut".
        const string Other = """{ "mcpServers": { "ourcut": { "command": "python", "args": ["cut.py"] } } }""";
        result = DesktopConfigFile.Merge(Other, Exe, ["mcp"]);
        Assert.False(result.RemovedOurCut);
        Assert.Equal(["ourcut", "highlightcut"], Servers(result.Json!).Select(p => p.Key));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_read()
    {
        const string Text = """
            // Claude Desktop
            {
              /* servers */
              "mcpServers": {
                "other": { "command": "x", "args": ["y",], },
              },
            }
            """;
        var result = DesktopConfigFile.Merge(Text, Exe, ["mcp"]);

        Assert.Null(result.Error);
        var servers = Servers(result.Json!);
        Assert.Equal(["other", "highlightcut"], servers.Select(p => p.Key));
        Assert.Equal("x", (string?)servers["other"]!["command"]);
    }

    [Fact]
    public void A_bom_is_read_and_the_file_is_written_without_one()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, """{ "mcpServers": { "other": { "command": "x" } } }""", new UTF8Encoding(true));

        Assert.Equal(DesktopConfigOutcome.Updated, DesktopConfigFile.Add(File_, Exe, ["mcp"]).Outcome);

        byte[] bytes = File.ReadAllBytes(File_);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal(["other", "highlightcut"], Servers(Encoding.UTF8.GetString(bytes)).Select(p => p.Key));
        Assert.Equal(0xEF, File.ReadAllBytes(File_ + ".bak")[0]);
        Assert.True(DesktopConfigFile.Merge("\uFEFF{}", Exe, ["mcp"]).Changed);
    }

    [Fact]
    public void A_non_ascii_path_is_written_as_it_is()
    {
        const string Path_ = @"C:\Users\Анна\Programs\HighlightCut — test\HighlightCut.exe";
        var result = DesktopConfigFile.Add(File_, Path_, ["mcp"]);

        Assert.Equal(DesktopConfigOutcome.Created, result.Outcome);
        string text = File.ReadAllText(File_);
        Assert.Contains(@"C:\\Users\\Анна\\Programs\\HighlightCut — test\\HighlightCut.exe", text, StringComparison.Ordinal);
        AssertHighlightCut(Servers(text), Path_);
    }

    [Theory]
    [InlineData("{ \"mcpServers\": { \"a\": ", "isn’t valid JSON")]
    [InlineData("not json", "isn’t valid JSON")]
    [InlineData("[]", "doesn’t have the settings object Claude Desktop expects")]
    [InlineData("{ \"mcpServers\": [] }", "has an “mcpServers” that isn’t a list of servers")]
    [InlineData("{ \"mcpServers\": null }", "has an “mcpServers” that isn’t a list of servers")]
    public void A_file_that_cannot_be_read_is_not_changed(string text, string error)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, text);

        var result = DesktopConfigFile.Add(File_, Exe, ["mcp"]);

        Assert.Equal(DesktopConfigOutcome.Failed, result.Outcome);
        Assert.Equal(error, result.Error);
        Assert.Equal(text, File.ReadAllText(File_));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(File_)!));
    }

    [Fact]
    public void The_store_apps_file_is_found_in_its_package()
    {
        string local = Path.Combine(_dir, "Local");
        string store = Path.Combine(local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude");
        Directory.CreateDirectory(store);
        Directory.CreateDirectory(Path.Combine(local, "Packages", "Claude_other"));
        Directory.CreateDirectory(Path.Combine(local, "Packages", "Microsoft.Something", "LocalCache", "Roaming", "Claude"));

        Assert.Equal([File_, Path.Combine(store, "claude_desktop_config.json")], DesktopConfigFile.Files(File_, local));
        Assert.Equal([File_], DesktopConfigFile.Files(File_, null));
        Assert.Equal([File_], DesktopConfigFile.Files(File_, Path.Combine(_dir, "missing")));
    }
}
