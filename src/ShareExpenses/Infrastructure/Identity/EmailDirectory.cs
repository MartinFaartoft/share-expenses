using Microsoft.AspNetCore.Identity;
using ShareExpenses.Shared;

namespace ShareExpenses.Infrastructure.Identity;

/// <summary>
/// Which account an email address belongs to, and the reverse. Identity is the only
/// place that knows a user's address; the ledger records none (spec §11). Every
/// account's address is verified: accounts are created only by signing in with a
/// code sent to it (spec §4). Answers are lookups: they feed guards, or propose
/// candidates the stream confirms — never invariants.
/// </summary>
public interface IEmailDirectory
{
    Task<UserId?> AccountFor(string email, CancellationToken ct = default);

    Task<string?> AddressOf(UserId user, CancellationToken ct = default);
}

internal sealed class IdentityEmailDirectory(UserManager<User> users) : IEmailDirectory
{
    public async Task<UserId?> AccountFor(string email, CancellationToken ct = default) =>
        // FindByEmailAsync compares Identity's normalised (upper-cased) form: case-insensitive.
        await users.FindByEmailAsync(email) is { } user ? UserId.From(user.Id) : null;

    public async Task<string?> AddressOf(UserId user, CancellationToken ct = default) =>
        (await users.FindByIdAsync(user.Value.ToString()))?.Email;
}
