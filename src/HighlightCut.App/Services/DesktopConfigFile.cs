using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HighlightCut.App.Services;

/// <summary>What <see cref="DesktopConfigFile.Add"/> did to a claude_desktop_config.json.</summary>
public enum DesktopConfigOutcome
{
    /// <summary>There was no file; it was written with HighlightCut in it.</summary>
    Created,

    /// <summary>HighlightCut was added, or its entry replaced; everything else was kept.</summary>
    Updated,

    /// <summary>The file already had this exact entry; it was not touched.</summary>
    AlreadyAdded,

    /// <summary>The file could not be read, understood or written; it was not changed.</summary>
    Failed,
}

/// <param name="RemovedOurCut">An <c>ourcut</c> entry that started the old program was taken out.</param>
/// <param name="Error">Why it failed, as a sentence end ("isn’t valid JSON").</param>
public sealed record DesktopConfigResult(string File, DesktopConfigOutcome Outcome, bool RemovedOurCut = false, string? Error = null);

/// <summary>
/// Claude Desktop's claude_desktop_config.json: finds it (also for the Microsoft Store app) and puts HighlightCut's entry
/// in it, keeping every other key and server. The previous file is kept as claude_desktop_config.json.bak.
/// </summary>
public static class DesktopConfigFile
{
    public const string FileName = "claude_desktop_config.json";
    public const string ServerName = "highlightcut";
    public const string LegacyServerName = "ourcut";

    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    // Two-space indents like Claude Desktop writes; non-ASCII paths stay readable.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The files to add HighlightCut to: the usual one (made if missing), and on Windows the Microsoft Store app's, which
    /// lives in its package folder (<c>%LOCALAPPDATA%\Packages\Claude_…\LocalCache\Roaming\Claude</c>) when that exists.
    /// </summary>
    public static IReadOnlyList<string> Files(string usual, string? localAppData)
    {
        var files = new List<string> { usual };
        if (localAppData is null || !Directory.Exists(Path.Combine(localAppData, "Packages")))
            return files;
        try
        {
            foreach (string package in Directory.EnumerateDirectories(Path.Combine(localAppData, "Packages"), "Claude_*"))
            {
                string folder = Path.Combine(package, "LocalCache", "Roaming", "Claude");
                if (Directory.Exists(folder))
                    files.Add(Path.Combine(folder, FileName));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No Store app to look at.
        }
        return files;
    }

    /// <summary>
    /// Adds (or replaces) HighlightCut's entry in <paramref name="file"/>, or writes the file with only that entry. The file
    /// is replaced in one step, after the previous one is saved as .bak; if anything fails it is left as it was.
    /// </summary>
    public static DesktopConfigResult Add(string file, string command, IReadOnlyList<string> args)
    {
        try
        {
            bool exists = File.Exists(file);
            // ReadAllText drops a UTF-8 BOM.
            string? text = exists ? File.ReadAllText(file) : null;
            var merge = Merge(text, command, args);
            if (merge.Json is null)
                return new(file, DesktopConfigOutcome.Failed, Error: merge.Error);
            if (!merge.Changed)
                return new(file, DesktopConfigOutcome.AlreadyAdded);

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                File.WriteAllText(temp, merge.Json, new UTF8Encoding(false));
                if (exists)
                    File.Copy(file, file + ".bak", overwrite: true);
                File.Move(temp, file, overwrite: true);
            }
            finally
            {
                File.Delete(temp);
            }
            return new(file, exists ? DesktopConfigOutcome.Updated : DesktopConfigOutcome.Created, merge.RemovedOurCut);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(file, DesktopConfigOutcome.Failed, Error: "couldn’t be written (" + e.Message.TrimEnd('.') + ")");
        }
    }

    /// <summary>What <see cref="Merge"/> made of a file's text: the new text (null if it can't be changed safely) and why.</summary>
    public sealed record MergeResult(string? Json, bool Changed, bool RemovedOurCut, string? Error);

    /// <summary>
    /// The file's text with HighlightCut's entry in <c>mcpServers</c>, other keys and servers as they were (comments are
    /// dropped: the JSON is written again). An <c>ourcut</c> entry that starts OurCut is removed. Null text is no file.
    /// </summary>
    public static MergeResult Merge(string? text, string command, IReadOnlyList<string> args)
    {
        JsonObject root;
        string trimmed = (text ?? "").TrimStart('﻿');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            root = [];
        }
        else
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(trimmed, documentOptions: ReadOptions);
            }
            catch (JsonException)
            {
                return new(null, false, false, "isn’t valid JSON");
            }
            if (node is not JsonObject obj)
                return new(null, false, false, "doesn’t have the settings object Claude Desktop expects");
            root = obj;
        }

        JsonObject servers;
        switch (root["mcpServers"])
        {
            case JsonObject existing:
                servers = existing;
                break;
            case null when !root.ContainsKey("mcpServers"):
                servers = [];
                root["mcpServers"] = servers;
                break;
            default:
                return new(null, false, false, "has an “mcpServers” that isn’t a list of servers");
        }

        var entry = new JsonObject
        {
            ["command"] = command,
            ["args"] = new JsonArray([.. args.Select(a => (JsonNode?)JsonValue.Create(a))]),
        };

        bool changed = false;
        // The same name in other case would be a second HighlightCut.
        foreach (string name in servers.Select(s => s.Key).Where(k => k != ServerName && k.Equals(ServerName, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            servers.Remove(name);
            changed = true;
        }
        if (!(servers[ServerName] is JsonObject current && JsonNode.DeepEquals(current, entry)))
        {
            // Replacing keeps the entry where it was.
            servers[ServerName] = entry;
            changed = true;
        }

        bool removedOurCut = false;
        foreach (var (name, value) in servers.ToList())
        {
            if (name.Equals(LegacyServerName, StringComparison.OrdinalIgnoreCase) && StartsOurCut(value))
            {
                servers.Remove(name);
                removedOurCut = changed = true;
            }
        }

        return new(root.ToJsonString(WriteOptions) + "\n", changed || text is null, removedOurCut, null);
    }

    // The old entry: its command is OurCut's program (OurCut.exe, or a path through an OurCut folder).
    private static bool StartsOurCut(JsonNode? server) =>
        server is JsonObject obj && obj["command"] is JsonValue value && value.TryGetValue(out string? command)
        && command.Replace('\\', '/').Split('/').Any(part => Path.GetFileNameWithoutExtension(part).Equals("OurCut", StringComparison.OrdinalIgnoreCase));
}
