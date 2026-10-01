using System.Security.Cryptography;
using System.Text;

namespace ShareExpenses.Shared;

/// <summary>
/// Invite link tokens (spec §4, §11). The raw token exists only in the link — the
/// response and the email. What is recorded, on <c>MemberInvited</c>, is its
/// SHA-256: irreversible, and safe to keep forever, because a token is 256 random
/// bits and cannot be guessed from its hash. Unsalted on purpose: the hash is what
/// a presented link is matched against.
/// </summary>
internal static class InviteToken
{
    /// <summary>A fresh token (43 URL-safe characters) and the hash to record.</summary>
    public static (string Token, string Hash) Generate()
    {
        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return (token, Hash(token));
    }

    /// <summary>SHA-256 of the token, as 64 lowercase hex characters.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Whether a presented token matches a recorded hash, in constant time.</summary>
    public static bool Matches(string? token, string recordedHash) =>
        token is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(recordedHash));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
