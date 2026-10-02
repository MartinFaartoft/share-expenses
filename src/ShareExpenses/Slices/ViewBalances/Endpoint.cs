using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Authorization;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;
using Wolverine.Http;

namespace ShareExpenses.Slices.ViewBalances;

/// <summary>
/// <c>GET /groups/{group}</c> — the Balances screen: the group page.
///
/// Loads the stored <c>group_ledger</c> document, projected inline with the events
/// (spec §11), and shapes it for the caller. Nothing is folded here.
/// </summary>
public static class Endpoint
{
    public const string GroupNotFound = "group not found";

    [WolverineGet("/groups/{group}", Name = "ViewBalances")]
    public static async Task<IResult> Get(
        string group, ClaimsPrincipal user, IQuerySession session, TimeProvider clock, CancellationToken ct)
    {
        // A malformed id is answered like a missing group: nothing is disclosed either way.
        var state = GroupId.TryParse(group, out var groupId) ? await session.LoadAsync<State>(groupId.Value, ct) : null;

        return Reader.Read(state, new Query(user.UserId(), clock.GetUtcNow())) is { } ledger
            ? Results.Ok(ledger)
            : Results.Problem(GroupNotFound, statusCode: StatusCodes.Status404NotFound);
    }
}
