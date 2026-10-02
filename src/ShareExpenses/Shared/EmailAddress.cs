namespace ShareExpenses.Shared;

/// <summary>
/// Email addresses as typed: trimmed, case kept (the local part is technically
/// case-sensitive), and checked only for plausibility. Real validation is whether
/// mail arrives — and, for sign-in, whether its code comes back (spec §4).
/// </summary>
internal static class EmailAddress
{
    private const int MaxLength = 254;

    /// <summary>Trimmed; case kept.</summary>
    public static string Trim(string? email) => email?.Trim() ?? "";

    /// <summary>
    /// For matching: case-insensitive, as Identity's default <c>UpperInvariantLookupNormalizer</c>
    /// does it, so an invite's address and an account's address compare the same way.
    /// </summary>
    public static string Normalize(string email) => email.Trim().Normalize().ToUpperInvariant();

    /// <summary>At most 254 characters, exactly one @ with something on both sides, no whitespace.</summary>
    public static bool IsPlausible(string email) =>
        email.Length is > 0 and <= MaxLength
        && !email.Any(char.IsWhiteSpace)
        && email.Split('@') is [{ Length: > 0 }, { Length: > 0 }];
}
