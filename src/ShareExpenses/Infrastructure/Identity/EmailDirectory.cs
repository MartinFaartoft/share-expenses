using Microsoft.AspNetCore.Identity;
using ShareExpenses.Shared;

namespace ShareExpenses.Infrastructure.Identity;

/// <summary>
/// Which account an email address belongs to, today. Identity is the only place
/// that knows a user's current address; the ledger records none (spec §11).
/// Answers feed guards only, never invariants.
/// </summary>
public interface IEmailDirectory
{
    Task<UserId?> AccountFor(string email, CancellationToken ct = default);
}

internal sealed class IdentityEmailDirectory(UserManager<User> users) : IEmailDirectory
{
    public async Task<UserId?> AccountFor(string email, CancellationToken ct = default) =>
        // FindByEmailAsync compares Identity's normalised (upper-cased) form: case-insensitive.
        await users.FindByEmailAsync(email) is { } user ? UserId.From(user.Id) : null;
}
