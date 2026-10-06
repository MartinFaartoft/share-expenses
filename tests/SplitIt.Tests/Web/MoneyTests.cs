using SplitIt.Web;

namespace SplitIt.Tests.Web;

/// <summary>Amounts as shown on screens: minor units to the currency's decimals, with its symbol.</summary>
public class MoneyTests
{
    [Theory]
    [InlineData(14000, "GBP", "£140.00")]
    [InlineData(950, "EUR", "€9.50")]
    [InlineData(1, "USD", "$0.01")]
    [InlineData(123456789, "GBP", "£1,234,567.89")]
    [InlineData(1200, "JPY", "¥1,200")]
    [InlineData(1500, "KWD", "KWD 1.500")]
    [InlineData(-3000, "GBP", "£30.00")]
    public void Formats_minor_units_with_the_currencys_decimals_and_symbol(long minor, string currency, string expected) =>
        Assert.Equal(expected, Money.Format(minor, currency));

    [Fact]
    public void A_currency_whose_symbol_is_letters_is_shown_by_its_code() =>
        Assert.Equal("CHF 12.50", Money.Format(1250, "CHF"));

    [Theory]
    [InlineData("120.50", "GBP", 12050)]
    [InlineData("120,50", "GBP", 12050)]
    [InlineData("120", "GBP", 12000)]
    [InlineData("0.5", "GBP", 50)]
    [InlineData(" 9.5 ", "EUR", 950)]
    [InlineData("1200", "JPY", 1200)]
    [InlineData("1.500", "KWD", 1500)]
    [InlineData("0", "GBP", 0)]
    public void Reads_what_was_typed_in_the_currencys_units(string text, string currency, long expected)
    {
        Assert.True(Money.TryParse(text, currency, out var minor));
        Assert.Equal(expected, minor);
    }

    [Theory]
    [InlineData("", "GBP")]
    [InlineData("abc", "GBP")]
    [InlineData("12.345", "GBP")]
    [InlineData("1,234.50", "GBP")]
    [InlineData("1,200", "JPY")]
    [InlineData("1.5.0", "GBP")]
    [InlineData("-5", "GBP")]
    [InlineData(".5", "GBP")]
    [InlineData("5.", "GBP")]
    [InlineData("99999999999999999999", "GBP")]
    public void Does_not_read_anything_else(string text, string currency) =>
        Assert.False(Money.TryParse(text, currency, out _));

    [Fact]
    public void Nothing_typed_is_not_an_amount() =>
        Assert.False(Money.TryParse(null, "GBP", out _));
}
