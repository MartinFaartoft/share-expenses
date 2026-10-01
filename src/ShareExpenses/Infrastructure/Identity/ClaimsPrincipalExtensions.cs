using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using ShareExpenses.Shared;

namespace ShareExpenses.Infrastructure.Identity;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The signed-in user's id. Only valid behind <c>RequireAuthorization</c>: an
    /// authenticated principal without one is a configuration bug, not a client error.
    /// </summary>
    public static UserId UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? Shared.UserId.From(id)
            : throw new InvalidOperationException(
                $"Authenticated principal has no Guid {nameof(ClaimTypes.NameIdentifier)} claim; " +
                $"is the endpoint missing RequireAuthorization, or is {nameof(IdentityOptions)} misconfigured?");
}
