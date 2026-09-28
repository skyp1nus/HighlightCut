using System.Buffers;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HighlightCut.Media.Export;

/// <summary>Export file names from a pattern such as "{project}-cut-{n}".</summary>
public static partial class ExportFileNames
{
    public const string DefaultPattern = "{project}-cut-{n}";

    /// <summary>What a pattern can hold: the project name, the clip number, the clip label and the date.</summary>
    public static IReadOnlyList<string> Tokens { get; } = ["{project}", "{n}", "{label}", "{date}"];

    // Windows' rules on every system, so a pattern names files the same everywhere.
    private static readonly SearchValues<char> Invalid = SearchValues.Create([.. Path.GetInvalidFileNameChars(), .. "/\\:*?\"<>|"]);

    /// <summary>
    /// The file name without extension. Merged files drop {n} and {label}, each with one separator before it;
    /// {n} has two digits at least and {label} is the label's slug.
    /// </summary>
    public static string Fill(string pattern, string project, int number, string label, DateOnly date, bool merged)
    {
        string name = merged ? LabelToken().Replace(NumberToken().Replace(pattern, ""), "") : pattern;
        name = Token().Replace(name, m => m.Groups[1].Value switch
        {
            "project" => project,
            "n" => number.ToString("00", CultureInfo.InvariantCulture),
            "label" => ExportPlanner.Slug(label),
            _ => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });
        string clean = string.Concat(name.Select(c => char.IsControl(c) || Invalid.Contains(c) ? '-' : c)).Trim();
        return clean.Length == 0 ? "untitled" : clean;
    }

    [GeneratedRegex(@"\{(project|n|label|date)\}")]
    private static partial Regex Token();

    [GeneratedRegex(@"[-_. ]?\{n\}")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"[-_. ]?\{label\}")]
    private static partial Regex LabelToken();
}
