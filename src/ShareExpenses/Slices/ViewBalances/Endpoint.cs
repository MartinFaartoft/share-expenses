using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;
using ShareExpenses.Web;
using Wolverine.Http;

namespace ShareExpenses.Slices.ViewBalances;

/// <summary>
/// <c>GET /groups/{group}/balances</c>, the screen; <c>GET /api/groups/{group}/balances</c>,
/// the same read model as JSON.
///
/// Folds the group stream live per request: nothing stored that could go stale (spec §11).
/// </summary>
public static class Endpoint
{
    public const string GroupNotFound = "group not found";

    [WolverineGet("/api/groups/{group}/balances", Name = "ViewBalances")]
    public static async Task<IResult> Get(
        string group, ClaimsPrincipal user, IQuerySession session, TimeProvider clock, CancellationToken ct) =>
        await Read(group, user, session, clock, ct) is { } balances
            ? Results.Ok(balances)
            : Results.Problem(GroupNotFound, statusCode: StatusCodes.Status404NotFound);

    [WolverineGet("/groups/{group}/balances", Name = "BalancesPage")]
    public static async Task<IResult> Page(
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
