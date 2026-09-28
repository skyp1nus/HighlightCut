using HighlightCut.Media.Ffmpeg;

namespace HighlightCut.Media.Tests.Ffmpeg;

public class FfmpegTextTests
{
    [Theory]
    [InlineData(0, "0.000000")]
    [InlineData(12.04, "12.040000")]
    [InlineData(90000.5, "90000.500000")]
    [InlineData(-1, "0.000000")]
    public void Seconds_uses_six_invariant_decimals(double t, string expected) =>
        Assert.Equal(expected, FfmpegText.Seconds(t));

    [Fact]
    public void Seconds_ignores_culture_decimal_separator()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.500000", FfmpegText.Seconds(1.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Seconds_rejects_non_finite_values(double t) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegText.Seconds(t));
}
