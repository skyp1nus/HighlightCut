using HighlightCut.Media.Export;

namespace HighlightCut.Media.Tests.Export;

public class ExportFileNamesTests
{
    private static readonly DateOnly Date = new(2026, 9, 25);

    private static string Fill(string pattern, int number = 1, string label = "Cold open", bool merged = false) =>
        ExportFileNames.Fill(pattern, "interview_final_v3", number, label, Date, merged);

    [Fact]
    public void The_default_pattern_numbers_separate_files_and_not_the_merged_one()
    {
        Assert.Equal("interview_final_v3-cut", Fill(ExportFileNames.DefaultPattern, merged: true));
        Assert.Equal("interview_final_v3-cut-01", Fill(ExportFileNames.DefaultPattern));
        Assert.Equal("interview_final_v3-cut-04", Fill(ExportFileNames.DefaultPattern, 4));
    }

    [Theory]
    [InlineData(1, "01")]
    [InlineData(9, "09")]
    [InlineData(12, "12")]
    [InlineData(100, "100")]
    public void Numbers_have_two_digits_at_least(int number, string text) => Assert.Equal(text, Fill("{n}", number));

    [Theory]
    [InlineData("{project}-{n}", "interview_final_v3")]
    [InlineData("{project}_{label}", "interview_final_v3")]
    [InlineData("{project}.{n}", "interview_final_v3")]
    [InlineData("{project} {label}", "interview_final_v3")]
    [InlineData("{project}--{n}", "interview_final_v3-")]
    [InlineData("{n}-{project}", "-interview_final_v3")]
    [InlineData("{project}-{n}-{label}-{date}", "interview_final_v3-2026-09-25")]
    public void Merged_names_drop_the_number_and_label_with_one_separator(string pattern, string name) =>
        Assert.Equal(name, Fill(pattern, merged: true));

    [Fact]
    public void Labels_become_slugs_that_keep_any_script()
    {
        Assert.Equal("cold-open", Fill("{label}"));
        Assert.Equal("вступ-і-план", Fill("{label}", label: "Вступ і план"));
        Assert.Equal("clip", Fill("{label}", label: ""));
    }

    [Fact]
    public void Dates_are_year_month_day() => Assert.Equal("2026-09-25_interview_final_v3", Fill("{date}_{project}"));

    [Fact]
    public void Characters_files_cannot_hold_become_dashes()
    {
        Assert.Equal("a-b-c-d-e-f-g-h-i-j", Fill("a/b\\c:d*e?f\"g<h>i|j"));
        Assert.Equal("q-a-interview_final_v3", ExportFileNames.Fill("q?a-{project}", "interview_final_v3", 1, "", Date, merged: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{n}")]
    public void An_empty_name_is_untitled(string pattern) => Assert.Equal("untitled", Fill(pattern, merged: true));

    [Fact]
    public void Unknown_braces_are_kept() => Assert.Equal("{take}-interview_final_v3", Fill("{take}-{project}"));
}
