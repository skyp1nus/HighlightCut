using HighlightCut.Core.Transcripts;

namespace HighlightCut.Core.Tests.Transcripts;

public class TranscriptLayoutTests
{
    /// <summary>Words half a second long, <paramref name="gaps"/>[i] seconds before word i + 1 (0.1 s when not given).</summary>
    internal static List<Word> Words(string text, params (int After, double Gap)[] gaps)
    {
        var words = new List<Word>();
        double t = 0;
        string[] tokens = text.Split(' ');
        for (int i = 0; i < tokens.Length; i++)
        {
            words.Add(new Word(tokens[i], t, t + 0.5));
            t += 0.5 + (gaps.FirstOrDefault(g => g.After == i) is { Gap: > 0 } g ? g.Gap : 0.1);
        }
        return words;
    }

    private static IEnumerable<string> Texts(IEnumerable<Phrase> phrases) => phrases.Select(p => p.Text);

    [Fact]
    public void Paragraphs_start_after_a_sentence_and_a_pause()
    {
        // A phrase pause after "one." breaks; after "three" (no sentence end) it does not; a 2 s pause always breaks.
        var words = Words("Word one. Two three four Five six", (1, 0.9), (3, 0.9), (4, 2.1));

        var paragraphs = TranscriptLayout.Paragraphs(words);

        Assert.Equal(["Word one.", "Two three four", "Five six"], Texts(paragraphs));
        Assert.Equal([0, 2, 5], paragraphs.Select(p => p.FirstWord));
        Assert.Equal(words[2].Start, paragraphs[1].Start);
        Assert.Equal(words[4].End, paragraphs[1].End);
    }

    [Fact]
    public void A_short_pause_after_a_sentence_does_not_break()
    {
        var words = Words("One. Two.", (0, 0.5));

        Assert.Single(TranscriptLayout.Paragraphs(words));
    }

    [Fact]
    public void A_long_paragraph_ends_at_the_next_sentence()
    {
        string text = string.Join(' ', Enumerable.Range(0, 130).Select(i => i == 124 ? "end." : "word")) + " next words";
        var words = Words(text);

        var paragraphs = TranscriptLayout.Paragraphs(words);

        Assert.Equal([0, 125], paragraphs.Select(p => p.FirstWord));
        Assert.Equal([125, 7], paragraphs.Select(p => p.WordCount));
    }

    [Fact]
    public void No_words_no_paragraphs()
    {
        Assert.Empty(TranscriptLayout.Paragraphs([]));
        Assert.Empty(TranscriptLayout.Chunks([], []));
    }

    [Fact]
    public void Chunks_follow_punctuation_or_seven_words_and_stay_inside_paragraphs()
    {
        // "Hi," is too short to end a chunk; "one two three," ends one; seven words end one; a paragraph end ends one.
        var words = Words("Hi, one two three, a b c d e f g h i. New start", (12, 2.1));

        var chunks = TranscriptLayout.Chunks(words, TranscriptLayout.Paragraphs(words));

        Assert.Equal(["Hi, one two three,", "a b c d e f g", "h i.", "New start"], Texts(chunks));
        Assert.Equal([0, 4, 11, 13], chunks.Select(c => c.FirstWord));
    }

    [Fact]
    public void Sentences_end_in_a_full_stop_question_exclamation_or_ellipsis()
    {
        Assert.True(TranscriptLayout.EndsSentence("done."));
        Assert.True(TranscriptLayout.EndsSentence("why?"));
        Assert.True(TranscriptLayout.EndsSentence("wow!"));
        Assert.True(TranscriptLayout.EndsSentence("so…"));
        Assert.False(TranscriptLayout.EndsSentence("and,"));
        Assert.False(TranscriptLayout.EndsSentence(""));
    }
}
