using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;
using ShareExpenses.Web;
using Wolverine.Http;

namespace ShareExpenses.Slices.ViewBalances;

/// <summary>
/// <c>GET /groups/{group}/balances</c>, the Balances screen.
///
/// Folds the group stream live per request: nothing stored that could go stale (spec §11).
/// </summary>
public static class Endpoint
{
    [WolverineGet("/groups/{group}/balances")]
    public static async Task<IResult> Get(
        string group, ClaimsPrincipal user, IQuerySession session, TimeProvider clock, CancellationToken ct) =>
        await Read(group, user, session, clock, ct) is { } balances
            ? new RazorComponentResult<BalancesPage>(new { Model = balances })
            // A non-member, a missing group and a malformed id: the same page, the same 404.
            : new RazorComponentResult<NotFoundPage>(new { What = "group" }) { StatusCode = StatusCodes.Status404NotFound };

    private static async Task<GroupBalancesReadModel?> Read(
        string group, ClaimsPrincipal user, IQuerySession session, TimeProvider clock, CancellationToken ct)
    {
        // A malformed id is answered like a missing group: nothing is disclosed either way.
        var state = GroupId.TryParse(group, out var groupId)
            ? await session.Events.AggregateStreamAsync<State>(groupId.Value, token: ct)
            : null;
        return Reader.Read(state, new Query(user.UserId(), clock.GetUtcNow()));
    }
}
