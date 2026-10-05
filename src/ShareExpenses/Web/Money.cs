using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using ShareExpenses.Shared;

namespace ShareExpenses.Web;

/// <summary>
/// Amounts as people read them: £140.00, €9.50, ¥1,200. The amount is an integer in the
/// currency's minor unit (spec §6), shown with that currency's decimal places — the
/// only place minor units become decimals. The symbol is the one .NET knows for the
/// ISO code, else the code itself (CHF 140.00).
/// </summary>
public static partial class Money
{
    private static readonly FrozenDictionary<string, string> Symbols = KnownSymbols();

    /// <param name="minor">In the currency's minor unit; shown without its sign.</param>
    /// <param name="currency">An ISO 4217 code the group was created with.</param>
    public static string Format(long minor, string currency)
    {
        var places = Currency.MinorUnitsOf(currency);
        var amount = Math.Abs((decimal)minor) / (decimal)Math.Pow(10, places);
        var number = amount.ToString("N" + places, CultureInfo.InvariantCulture);
        return Symbols.TryGetValue(currency, out var symbol) ? symbol + number : currency + " " + number;
    }

    /// <summary>
    /// An amount as typed, in the currency's units ("120.50"), to minor units. Digits with
    /// at most one <c>.</c> or <c>,</c> and no more decimals than the currency has; no
    /// thousands separators, which would be ambiguous.
    /// </summary>
    public static bool TryParse(string? text, string currency, out long minor)
    {
        minor = 0;
        var places = Currency.MinorUnitsOf(currency);
        var match = Typed().Match(text?.Trim() ?? "");
        if (!match.Success)
            return false;
        var fraction = match.Groups["fraction"].Value;
        if (fraction.Length > places)
            return false;
        return long.TryParse(match.Groups["whole"].Value + fraction.PadRight(places, '0'),
            NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }

    [GeneratedRegex(@"^(?<whole>\d+)(?:[.,](?<fraction>\d+))?$")]
    private static partial Regex Typed();

    /// <summary>A currency as a picker names it: the code, then the symbol amounts are shown with, if any (GBP £, DKK).</summary>
    public static string Label(string currency) =>
        Symbols.TryGetValue(currency, out var symbol) ? $"{currency} {symbol}" : currency;

    /// <summary>
    /// One symbol per currency, from the regions .NET knows, kept only when it contains a
    /// currency sign (£ € $ ¥ ₹ …) and otherwise only Latin letters (R$, US$): a symbol
    /// of letters ("kr", "zł") or in another script reads worse in a left-to-right line
    /// than the code. Without culture data (invariant globalization), there are none,
    /// and codes are shown.
    /// </summary>
    private static FrozenDictionary<string, string> KnownSymbols()
    {
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures).OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                var region = new RegionInfo(culture.Name);
                if (Readable(region.CurrencySymbol) && !symbols.ContainsKey(region.ISOCurrencySymbol))
                    symbols[region.ISOCurrencySymbol] = region.CurrencySymbol;
            }
        }
        catch (Exception e) when (e is CultureNotFoundException or ArgumentException)
        {
            // No culture data: codes everywhere.
        }

        // Where regions disagree, the home market's symbol.
        symbols["USD"] = "$";
        symbols["GBP"] = "£";
        symbols["EUR"] = "€";
        symbols["JPY"] = "¥";
        return symbols.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static bool Readable(string symbol) =>
        symbol.Any(c => char.GetUnicodeCategory(c) == UnicodeCategory.CurrencySymbol)
        && symbol.All(c => char.GetUnicodeCategory(c) == UnicodeCategory.CurrencySymbol || char.IsAsciiLetter(c));
}
