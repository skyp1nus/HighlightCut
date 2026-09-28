namespace HighlightCut.Core.Transcripts;

/// <summary>How a transcript is laid out for reading: paragraphs, and short chunks for the timeline lane.</summary>
public static class TranscriptLayout
{
    /// <summary>A pause at least this long always starts a paragraph.</summary>
    public const double ParagraphPause = 2.0;

    /// <summary>A paragraph with this many words ends at its next sentence end.</summary>
    public const int LongParagraph = 120;

    /// <summary>Most words in a lane chunk.</summary>
    public const int ChunkWords = 7;

    /// <summary>A chunk ends at punctuation only once it has this many words.</summary>
    public const int MinChunkWords = 3;

    /// <summary>
    /// Paragraphs: a new one starts after a pause of <see cref="ParagraphPause"/>, after a sentence followed by a pause of
    /// <see cref="Transcript.PhrasePause"/>, or after the sentence that makes a paragraph <see cref="LongParagraph"/> words long.
    /// </summary>
    public static IReadOnlyList<Phrase> Paragraphs(IReadOnlyList<Word> words)
    {
        var paragraphs = new List<Phrase>();
        int first = 0;
        for (int i = 1; i <= words.Count; i++)
        {
            if (i < words.Count && !StartsParagraph(words, first, i))
                continue;
            paragraphs.Add(Span(words, first, i - first));
            first = i;
        }
        return paragraphs;
    }

    private static bool StartsParagraph(IReadOnlyList<Word> words, int first, int i)
    {
        double pause = words[i].Start - words[i - 1].End;
        if (pause >= ParagraphPause)
            return true;
        return EndsSentence(words[i - 1].Text) && (pause >= Transcript.PhrasePause || i - first >= LongParagraph);
    }

    /// <summary>
    /// Short runs of words for the timeline lane, never across a paragraph: a chunk ends after punctuation once it has
    /// <see cref="MinChunkWords"/> words, at <see cref="ChunkWords"/> words, or at the paragraph's end.
    /// </summary>
    public static IReadOnlyList<Phrase> Chunks(IReadOnlyList<Word> words, IReadOnlyList<Phrase> paragraphs)
    {
        var chunks = new List<Phrase>();
        foreach (var paragraph in paragraphs)
        {
            int start = paragraph.FirstWord;
            int last = paragraph.FirstWord + paragraph.WordCount - 1;
            for (int i = start; i <= last; i++)
            {
                int count = i - start + 1;
                if ((EndsClause(words[i].Text) && count >= MinChunkWords) || count >= ChunkWords || i == last)
                {
                    chunks.Add(Span(words, start, count));
                    start = i + 1;
                }
            }
        }
        return chunks;
    }

    /// <summary>Ends in . ! ? or … (as <see cref="Transcript.Phrases"/> reads a sentence end).</summary>
    public static bool EndsSentence(string text) => text.Length > 0 && text[^1] is '.' or '!' or '?' or '…';

    private static bool EndsClause(string text) => text.Length > 0 && text[^1] is '.' or ',' or '?' or '!' or ':' or ';' or '…';

    private static Phrase Span(IReadOnlyList<Word> words, int first, int count) =>
        new(words[first].Start, words[first + count - 1].End,
            string.Join(' ', Enumerable.Range(first, count).Select(i => words[i].Text)), first, count);
}
