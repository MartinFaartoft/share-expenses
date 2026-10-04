using ShareExpenses.Web;

namespace ShareExpenses.Tests.Web;

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
}
