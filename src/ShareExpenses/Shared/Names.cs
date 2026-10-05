using System.Globalization;

namespace ShareExpenses.Shared;

internal static class Names
{
    public const int GroupNameMaxLength = 100;
    public const int DisplayNameMaxLength = 50;
    
    public static int VisibleLength(string name) => new StringInfo(name).LengthInTextElements;
    
    public static string ComparisonKey(string name) => name.Trim().ToUpperInvariant();
}
