using System.Globalization;

namespace ShareExpenses.Shared;

/// <summary>Rules for human-entered names: group names and member display names.</summary>
internal static class Names
{
    public const int MaxGroupName = 100;
    public const int MaxDisplayName = 50;

    /// <summary>
    /// Length as a person counts it: user-perceived characters (grapheme clusters),
    /// so "👩‍👩‍👧" or "é" written as e + accent count as one. Measure after trimming.
    /// </summary>
    public static int VisibleLength(string name) => new StringInfo(name).LengthInTextElements;

    /// <summary>
    /// The form two names are compared in: trimmed, case-insensitive. "Bob" and
    /// " bob " are the same name to anyone looking at a screen.
    /// </summary>
    public static string ComparisonKey(string name) => name.Trim().ToUpperInvariant();
}
