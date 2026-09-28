using HighlightCut.Core.Time;

namespace HighlightCut.Core.Tests.Time;

public class TimeFormatTests
{
    [Theory]
    [InlineData(0, "00:00:00.000")]
    [InlineData(301.42, "00:05:01.420")]
    [InlineData(872.48, "00:14:32.480")]
    [InlineData(3723.0005, "01:02:03.001")]
    [InlineData(-5, "00:00:00.000")]
    public void Timecode_formats_hours_minutes_seconds_millis(double t, string expected) =>
        Assert.Equal(expected, TimeFormat.Timecode(t));

    [Theory]
    [InlineData(118.6, "01:58.600")]
    [InlineData(12.04, "00:12.040")]
    [InlineData(6000, "100:00.000")]
    public void MinutesSeconds_does_not_wrap_minutes(double t, string expected) =>
        Assert.Equal(expected, TimeFormat.MinutesSeconds(t));

    [Theory]
    [InlineData(33.28, "0:33.280")]
    [InlineData(452.0, "7:32.000")]
    public void Duration_uses_unpadded_minutes(double t, string expected) =>
        Assert.Equal(expected, TimeFormat.Duration(t));

    [Theory]
    [InlineData(33.2, "33.200 s")]
    [InlineData(103.44, "1:43.440")]
    [InlineData(59.9996, "1:00.000")]
    public void ShortDuration_drops_the_minutes_under_a_minute(double t, string expected) =>
        Assert.Equal(expected, TimeFormat.ShortDuration(t));

    [Theory]
    [InlineData(872.48, "14:32")]
    [InlineData(59.999, "0:59")]
    public void WholeSeconds_truncates_to_whole_seconds(double t, string expected) =>
        Assert.Equal(expected, TimeFormat.WholeSeconds(t));

    [Theory]
    [InlineData("00:05:01.420", 301.42)]
    [InlineData("05:01.420", 301.42)]
    [InlineData("12.5", 12.5)]
    [InlineData("1:00:00", 3600)]
    public void TryParse_accepts_supported_forms(string text, double expected)
    {
        Assert.True(TimeFormat.TryParse(text, out double t));
        Assert.Equal(expected, t, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1:2:3:4")]
    [InlineData("00:61:00")]
    [InlineData("abc")]
    [InlineData("-1")]
    public void TryParse_rejects_invalid_text(string text) =>
        Assert.False(TimeFormat.TryParse(text, out _));

    [Fact]
    public void Formats_ignore_current_culture()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("uk-UA");
            Assert.Equal("00:00:01.500", TimeFormat.Timecode(1.5));
            Assert.True(TimeFormat.TryParse("1.5", out double t));
            Assert.Equal(1.5, t);
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }
}
