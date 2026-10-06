namespace SplitIt.Shared;

internal static class EmailAddress
{
    private const int MaxLength = 254;

    public static string Trim(string? email) => email?.Trim() ?? "";

    public static string Normalize(string email) => email.Trim().Normalize().ToUpperInvariant();

    public static bool IsPlausible(string email) =>
        email.Length is > 0 and <= MaxLength
        && !email.Any(char.IsWhiteSpace)
        && email.Split('@') is [{ Length: > 0 }, { Length: > 0 }];
}
