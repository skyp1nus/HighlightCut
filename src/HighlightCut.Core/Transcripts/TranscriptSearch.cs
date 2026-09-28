using System.Text;

namespace HighlightCut.Core.Transcripts;

/// <summary>A run of words: the index of the first one and how many.</summary>
public readonly record struct WordSpan(int First, int Count);

/// <summary>Finding text and filler words in a transcript, word by word.</summary>
public static class TranscriptSearch
{
    /// <summary>The word in lower case with only letters, digits, apostrophes and hyphens left ("Um," → "um").</summary>
    public static string Normalize(string word)
    {
        var text = new StringBuilder(word.Length);
        foreach (var rune in word.ToLowerInvariant().EnumerateRunes())
        {
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune) || rune.Value is '\'' or '’' or '-')
                text.Append(rune.ToString());
        }
        return text.ToString();
    }

    /// <summary>
    /// Where <paramref name="query"/> occurs. One word matches any word containing it; several match consecutive words,
    /// equal except the last, which only has to start the word. Case and punctuation are ignored.
    /// </summary>
    public static IReadOnlyList<WordSpan> Find(IReadOnlyList<Word> words, string query)
    {
        var tokens = Tokens(query);
        if (tokens.Length == 0)
            return [];
        var normalized = Normalized(words);
        var matches = new List<WordSpan>();
        for (int i = 0; i + tokens.Length <= normalized.Length; i++)
        {
            bool ok = true;
            for (int k = 0; k < tokens.Length && ok; k++)
            {
                string w = normalized[i + k];
                ok = k < tokens.Length - 1 ? w == tokens[k]
                    : tokens.Length == 1 ? w.Contains(tokens[k], StringComparison.Ordinal)
                    : w.StartsWith(tokens[k], StringComparison.Ordinal);
            }
            if (ok)
                matches.Add(new WordSpan(i, tokens.Length));
        }
        return matches;
    }

    /// <summary>
    /// Flags every word of every filler (one word or several, e.g. "you know") found in <paramref name="words"/>;
    /// a filler of several words never spans two paragraphs.
    /// </summary>
    public static bool[] MarkFillers(IReadOnlyList<Word> words, IEnumerable<string> fillers, IReadOnlyList<Phrase> paragraphs)
    {
        var flags = new bool[words.Count];
        var sequences = fillers.Select(Tokens).Where(t => t.Length > 0).ToList();
        if (sequences.Count == 0)
            return flags;
        var normalized = Normalized(words);
        var paragraphOf = ParagraphOf(words.Count, paragraphs);
        for (int i = 0; i < words.Count; i++)
        {
            foreach (var sequence in sequences)
            {
                if (i + sequence.Length > words.Count)
                    continue;
                bool ok = true;
                for (int k = 0; k < sequence.Length && ok; k++)
                    ok = paragraphOf[i + k] == paragraphOf[i] && normalized[i + k] == sequence[k];
                if (ok)
                    Array.Fill(flags, true, i, sequence.Length);
            }
        }
        return flags;
    }

    /// <summary>Runs of consecutive flagged words, each inside one paragraph.</summary>
    public static IReadOnlyList<WordSpan> Groups(IReadOnlyList<bool> flags, IReadOnlyList<Phrase> paragraphs)
    {
        var paragraphOf = ParagraphOf(flags.Count, paragraphs);
        var groups = new List<WordSpan>();
        for (int i = 0; i < flags.Count; i++)
        {
            if (!flags[i])
                continue;
            if (groups.Count > 0 && groups[^1] is var last && last.First + last.Count == i && paragraphOf[last.First] == paragraphOf[i])
                groups[^1] = last with { Count = last.Count + 1 };
            else
                groups.Add(new WordSpan(i, 1));
        }
        return groups;
    }

    private static string[] Tokens(string text) =>
        [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(t => t.Length > 0)];

    private static string[] Normalized(IReadOnlyList<Word> words)
    {
        var normalized = new string[words.Count];
        for (int i = 0; i < words.Count; i++)
            normalized[i] = Normalize(words[i].Text);
        return normalized;
    }

    /// <summary>The paragraph of each word; words outside every paragraph get -1.</summary>
    private static int[] ParagraphOf(int count, IReadOnlyList<Phrase> paragraphs)
    {
        var of = new int[count];
        Array.Fill(of, -1);
        for (int p = 0; p < paragraphs.Count; p++)
        {
            int end = Math.Min(count, paragraphs[p].FirstWord + paragraphs[p].WordCount);
            for (int i = Math.Max(0, paragraphs[p].FirstWord); i < end; i++)
                of[i] = p;
        }
        return of;
    }
}
