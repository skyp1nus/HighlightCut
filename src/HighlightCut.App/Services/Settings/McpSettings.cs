using System.Diagnostics.CodeAnalysis;

namespace HighlightCut.App.Services;

/// <summary>What Claude may do through the MCP server without asking.</summary>
[SuppressMessage("Naming", "CA1711", Justification = "A permission setting, not a code-access permission type.")]
public enum McpPermission
{
    Allow,
    Ask,
    Never,
}

/// <summary>Settings → MCP server. Permissions are kept as text, like the transcription choices, and checked on load.</summary>
/// <param name="Enabled">"Let Claude connect": the MCP server runs.</param>
/// <param name="OpenFiles">Allow or Ask.</param>
/// <param name="SaveProject">Allow or Ask.</param>
/// <param name="Export">Allow, Ask or Never.</param>
public sealed record McpSettings(
    bool Enabled = true,
    string OpenFiles = "Allow",
    string SaveProject = "Ask",
    string Export = "Ask");
