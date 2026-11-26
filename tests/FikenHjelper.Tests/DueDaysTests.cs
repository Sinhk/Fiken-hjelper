using FikenHjelper.Core.Invoicing;

namespace FikenHjelper.Tests;

public sealed class DueDaysTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("14", 14)]
    [InlineData(" 30 ", 30)]
    [InlineData("365", 365)]
    public void TryParse_accepts_whole_numbers_in_range(string text, int expected)
    {
        Assert.True(DueDays.TryParse(text, out var days));
        Assert.Equal(expected, days);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("14,5")]
    [InlineData("-1")]
    [InlineData("366")]
    [InlineData("99999999999")]
    public void TryParse_rejects_empty_text_non_integers_and_out_of_range(string? text)
    {
        Assert.False(DueDays.TryParse(text, out var days));
        Assert.Equal(0, days);
    }
}
