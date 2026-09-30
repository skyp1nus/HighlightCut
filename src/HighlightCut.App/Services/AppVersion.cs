using System.Reflection;

namespace HighlightCut.App.Services;

/// <summary>
/// The app's version: <c>Version</c> in Directory.Build.props (0.1.0), or with CI's <c>-dev.&lt;run&gt;</c> for a build
/// between releases. See docs/releasing.md.
/// </summary>
public static class AppVersion
{
    /// <summary>"0.1.0" or "0.1.0-dev.42": the informational version without the commit the SDK adds (+sha).</summary>
    public static string Text { get; } =
        Clean(typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The release this build belongs to: 0.1.0 for 0.1.0-dev.42.</summary>
    public static Version Release { get; } = ReleaseOf(Text) ?? new Version(0, 0, 0);

    internal static string Clean(string? informational)
    {
        string text = informational ?? "";
        int plus = text.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? text[..plus] : text;
    }

    /// <summary>X.Y.Z of a version ("0.1.0", "0.1.0-dev.42", "v0.1.0"); null for anything else.</summary>
    public static Version? ReleaseOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string core = text.Trim().TrimStart('v');
        int end = core.IndexOfAny(['-', '+']);
        if (end >= 0)
            core = core[..end];
        return Version.TryParse(core, out var v) && v.Build >= 0 && v.Revision < 0 ? v : null;
    }
}
