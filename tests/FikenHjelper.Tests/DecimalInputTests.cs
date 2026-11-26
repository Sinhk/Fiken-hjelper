using FikenHjelper.Core.Text;

namespace FikenHjelper.Tests;

public sealed class DecimalInputTests
{
    [Theory]
    [InlineData("12,5", "12.5")]
    [InlineData("12.5", "12.5")]
    [InlineData("350,50", "350.50")]
    [InlineData("350.5", "350.5")]
    [InlineData("0,5", "0.5")]
    [InlineData(",5", "0.5")]
    [InlineData(".5", "0.5")]
    [InlineData("12,", "12")]
    [InlineData("12.", "12")]
    [InlineData("7", "7")]
    [InlineData(" 7 ", "7")]
    [InlineData("1 234,50", "1234.50")]
    [InlineData("1 234.50", "1234.50")]
    [InlineData("1 234,5", "1234.5")]
    [InlineData("1 234,5", "1234.5")]
    [InlineData("1 234", "1234")]
    [InlineData("-3,25", "-3.25")]
    [InlineData("-3.25", "-3.25")]
    [InlineData("+3,25", "3.25")]
    [InlineData("−3,25", "-3.25")]
    [InlineData("0", "0")]
    [InlineData("000,50", "0.50")]
    public void TryParse_accepts_either_separator_and_ignores_spaces(string text, string expected)
    {
        Assert.True(DecimalInput.TryParse(text, out var value));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    [InlineData(".")]
    [InlineData("-")]
    [InlineData("1.234,5")]
    [InlineData("1,234.5")]
    [InlineData("1.234.567")]
    [InlineData("1,2,3")]
    [InlineData("1,2.3")]
    [InlineData("12,,5")]
    [InlineData("abc")]
    [InlineData("12kr")]
    [InlineData("1e3")]
    [InlineData("12,5 kr")]
    [InlineData("--3")]
    [InlineData("3-")]
    [InlineData("1-2")]
    [InlineData("0x10")]
    [InlineData("NaN")]
    [InlineData("∞")]
    [InlineData("１２")] // fullwidth digits are not accepted
    [InlineData("99999999999999999999999999999999")] // overflows decimal
    public void TryParse_rejects_empty_ambiguous_and_non_numeric_text(string? text)
    {
        Assert.False(DecimalInput.TryParse(text, out var value));
        Assert.Equal(0m, value);
    }

    [Fact]
    public void TryParse_rejects_absurdly_long_input_without_throwing()
    {
        Assert.False(DecimalInput.TryParse(new string('1', 5000), out _));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("0", false)]
    [InlineData("abc", false)]
    public void IsEmpty_is_true_only_for_blank_text(string? text, bool expected)
        => Assert.Equal(expected, DecimalInput.IsEmpty(text));

    [Fact]
    public void Format_uses_comma_and_no_trailing_zeros()
    {
        Assert.Equal("350", DecimalInput.Format(350m));
        Assert.Equal("350,5", DecimalInput.Format(350.50m));
        Assert.Equal("350,56", DecimalInput.Format(350.559m));
        Assert.Equal("0,5", DecimalInput.Format(0.5m));
        Assert.Equal("-3,25", DecimalInput.Format(-3.25m));
        Assert.Equal("12,346", DecimalInput.Format(12.3456m, maxDecimals: 3));
    }

    [Fact]
    public void Format_can_group_thousands_and_parse_reads_it_back()
    {
        var text = DecimalInput.Format(1234567.5m, grouped: true);

        Assert.Equal("1 234 567,5", text);
        Assert.True(DecimalInput.TryParse(text, out var value));
        Assert.Equal(1234567.5m, value);
    }

    [Fact]
    public void Format_and_parse_round_trip()
    {
        foreach (var amount in new[] { 350.5m, 0.01m, 12345.67m, -8.5m })
        {
            Assert.True(DecimalInput.TryParse(DecimalInput.Format(amount), out var value));
            Assert.Equal(amount, value);
        }
    }
}
