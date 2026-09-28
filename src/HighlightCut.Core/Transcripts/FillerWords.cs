namespace HighlightCut.Core.Transcripts;

/// <summary>
/// Filler words by language code ("en", "uk"): the defaults Settings → Transcription starts with (design
/// <c>DEF_FILLERS</c>), and the rules every list follows (trimmed, lower case, no duplicates).
/// </summary>
public static class FillerWords
{
    public static IReadOnlyList<string> English { get; } = ["um", "uh", "er", "like", "you know"];

    // Cyrillic "е" and an ASCII hyphen, as in the design.
    public static IReadOnlyList<string> Ukrainian { get; } = ["е-е", "ну", "типу", "короче"];

    /// <summary>The default lists, English first.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Defaults { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["en"] = English, ["uk"] = Ukrainian };

    /// <summary>
    /// Each word trimmed, inner spaces collapsed and lower-cased; empty words and repeats are dropped.
    /// </summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string> words)
    {
        var clean = new List<string>();
        // A hand-edited settings file can hold nulls.
        foreach (string? word in words)
        {
            if (word is null)
                continue;
            string w = string.Join(' ', word.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToLowerInvariant();
            if (w.Length > 0 && !clean.Contains(w, StringComparer.Ordinal))
                clean.Add(w);
        }
        return clean;
    }

    /// <summary>
    /// Saved lists over the defaults: every default language (its saved list, or the default one), then any
    /// other saved language. Every list is <see cref="Clean"/>ed.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> WithDefaults(IReadOnlyDictionary<string, IReadOnlyList<string>>? saved)
    {
        var lists = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (code, words) in Defaults)
            lists[code] = saved is not null && saved.TryGetValue(code, out var own) && own is not null ? Clean(own) : words;
        if (saved is not null)
        {
            foreach (var (code, words) in saved)
            {
                if (!lists.ContainsKey(code) && words is not null)
                    lists[code] = Clean(words);
            }
        }
        return lists;
    }

    /// <summary>The same languages with the same words in the same order.</summary>
    public static bool Same(IReadOnlyDictionary<string, IReadOnlyList<string>> a, IReadOnlyDictionary<string, IReadOnlyList<string>> b) =>
        a.Count == b.Count && a.All(l => b.TryGetValue(l.Key, out var words) && words.SequenceEqual(l.Value, StringComparer.Ordinal));

    public static bool AreDefaults(IReadOnlyDictionary<string, IReadOnlyList<string>> lists) => Same(lists, Defaults);

    /// <summary>Every language's words in one list, in order, each once (what a transcript marks).</summary>
    public static IReadOnlyList<string> All(IReadOnlyDictionary<string, IReadOnlyList<string>> lists) =>
        [.. lists.Values.SelectMany(w => w).Distinct(StringComparer.Ordinal)];
}
