using HighlightCut.Core.Transcripts;

namespace HighlightCut.Core.Tests.Transcripts;

public class FillerWordsTests
{
    [Fact]
    public void The_defaults_are_the_designs_lists_English_first()
    {
        Assert.Equal(["en", "uk"], FillerWords.Defaults.Keys);
        Assert.Equal(["um", "uh", "er", "like", "you know"], FillerWords.Defaults["en"]);
        Assert.Equal(["е-е", "ну", "типу", "короче"], FillerWords.Defaults["uk"]);
        Assert.True(FillerWords.AreDefaults(FillerWords.Defaults));
    }

    [Fact]
    public void Clean_trims_lower_cases_and_drops_empties_and_repeats()
    {
        Assert.Equal(["um", "you know", "ну"], FillerWords.Clean(["  Um ", "", "YOU   know", "um", "  ", "Ну"]));
        Assert.Equal(["um"], FillerWords.Clean(["um", null!]));
    }

    [Fact]
    public void Saved_lists_replace_their_language_and_keep_the_other_defaults()
    {
        Assert.True(FillerWords.AreDefaults(FillerWords.WithDefaults(null)));

        var lists = FillerWords.WithDefaults(new Dictionary<string, IReadOnlyList<string>>
        {
            ["de"] = ["Äh"],
            ["en"] = [" So ", "um"],
        });

        Assert.Equal(["en", "uk", "de"], lists.Keys);
        Assert.Equal(["so", "um"], lists["en"]);
        Assert.Equal(FillerWords.Ukrainian, lists["uk"]);
        Assert.Equal(["äh"], lists["de"]);
        Assert.False(FillerWords.AreDefaults(lists));
    }

    [Fact]
    public void Lists_are_the_same_only_with_the_same_words_in_the_same_order()
    {
        var reordered = new Dictionary<string, IReadOnlyList<string>> { ["en"] = ["uh", "um", "er", "like", "you know"], ["uk"] = FillerWords.Ukrainian };
        var copy = new Dictionary<string, IReadOnlyList<string>> { ["uk"] = [.. FillerWords.Ukrainian], ["en"] = [.. FillerWords.English] };

        Assert.False(FillerWords.AreDefaults(reordered));
        Assert.True(FillerWords.AreDefaults(copy));
        Assert.False(FillerWords.Same(copy, new Dictionary<string, IReadOnlyList<string>> { ["en"] = FillerWords.English }));
    }

    [Fact]
    public void All_lists_every_word_once_in_order()
    {
        var lists = new Dictionary<string, IReadOnlyList<string>> { ["en"] = ["um", "so"], ["de"] = ["äh", "so"] };
        Assert.Equal(["um", "so", "äh"], FillerWords.All(lists));
    }
}
