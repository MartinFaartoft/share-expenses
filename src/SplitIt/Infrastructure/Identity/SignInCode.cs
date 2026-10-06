namespace SplitIt.Infrastructure.Identity;

/// <summary>
/// The sign-in code for one address (spec §4): login state, so it lives with Identity
/// in EF's <c>identity</c> schema, never in the ledger. One row per address. A new
/// request replaces the code; a successful sign-in deletes the row.
///
/// The row outlives a dead code on purpose: it also counts the codes issued to the
/// address in the current window, so neither using up a code's attempts nor
/// requesting a fresh one hands out unlimited guesses.
/// </summary>
internal sealed class SignInCode
{
    /// <summary>The address, normalised as Identity does it: the key.</summary>
    public string NormalizedEmail { get; set; } = "";

    /// <summary>SHA-256 of the current code, or null once it is spent or dead.</summary>
    public string? CodeHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public int AttemptsLeft { get; set; }

    /// <summary>Codes issued to this address since <see cref="WindowStartedAt"/>.</summary>
    public int CodesIssued { get; set; }

    public DateTimeOffset WindowStartedAt { get; set; }

    /// <summary>Postgres <c>xmin</c>: two guesses at once cannot both spend the last attempt.</summary>
    public uint Version { get; set; }
}
