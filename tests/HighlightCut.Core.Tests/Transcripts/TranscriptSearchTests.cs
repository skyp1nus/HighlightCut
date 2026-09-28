using HighlightCut.Core.Transcripts;
using static HighlightCut.Core.Tests.Transcripts.TranscriptLayoutTests;

namespace HighlightCut.Core.Tests.Transcripts;

public class TranscriptSearchTests
{
    [Fact]
    public void Normalize_keeps_letters_digits_apostrophes_and_hyphens()
    {
        Assert.Equal("um", TranscriptSearch.Normalize("Um,"));
        Assert.Equal("that's", TranscriptSearch.Normalize("“That's”"));
        Assert.Equal("re-encoded", TranscriptSearch.Normalize("re-encoded."));
        Assert.Equal("4k", TranscriptSearch.Normalize("4K!"));
        Assert.Equal("е-е", TranscriptSearch.Normalize("Е-е…"));
    }

    [Fact]
    public void Search_matches_part_of_a_word_or_a_phrase_prefix()
    {
        var words = Words("Why cutting a video should take longer. Let's EXPORT. Export, cut a vid");

        Assert.Equal([new WordSpan(8, 1), new WordSpan(9, 1)], TranscriptSearch.Find(words, "xport"));
        Assert.Equal([new WordSpan(1, 1), new WordSpan(10, 1)], TranscriptSearch.Find(words, "cut"));
        Assert.Equal([new WordSpan(1, 3)], TranscriptSearch.Find(words, "Cutting a vid"));
        Assert.Equal([new WordSpan(10, 3)], TranscriptSearch.Find(words, "  cut A, vid "));
        Assert.Empty(TranscriptSearch.Find(words, "cutting video"));
        Assert.Empty(TranscriptSearch.Find(words, "   "));
        Assert.Empty(TranscriptSearch.Find(words, "?!"));
    }

    [Fact]
    public void Fillers_match_phrases_inside_one_paragraph()
    {
        // Paragraph break (2 s) between "you" and "know".
        var words = Words("Um, you know, it works. Е-е, so you know", (7, 2.1));
        var paragraphs = TranscriptLayout.Paragraphs(words);

        var flags = TranscriptSearch.MarkFillers(words, ["um", "you know", "е-е", "  "], paragraphs);

        Assert.Equal([true, true, true, false, false, true, false, false, false], flags);
    }

    [Fact]
    public void Fillers_never_join_across_a_paragraph()
    {
        var words = Words("It works. You know", (1, 2.1));

        Assert.Equal([false, false, false, false],
            TranscriptSearch.MarkFillers(words, ["works you"], TranscriptLayout.Paragraphs(words)));
    }

    [Fact]
    public void Groups_join_neighbouring_fillers()
    {
        var words = Words("Um uh so like. Um right", (3, 2.1));
        var paragraphs = TranscriptLayout.Paragraphs(words);
        var flags = TranscriptSearch.MarkFillers(words, FillerWords.All(FillerWords.Defaults), paragraphs);

        var groups = TranscriptSearch.Groups(flags, paragraphs);

        // "like." and the next "Um" touch, but a paragraph starts between them.
        Assert.Equal([new WordSpan(0, 2), new WordSpan(3, 1), new WordSpan(4, 1)], groups);
    }
}
