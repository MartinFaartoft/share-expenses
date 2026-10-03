using System.Security.Claims;
using Marten;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;
using Wolverine.Http;

namespace ShareExpenses.Slices.ViewSettlementPlan;

/// <summary>
/// <c>GET /groups/{group}/settlement-plan</c> — the Settle up screen's plan.
///
/// Folds the group stream live and computes the plan per request: never stored,
/// never an event (spec §10, §11). A plan that shifts between viewing and paying
/// costs nothing.
/// </summary>
public static class Endpoint
{
    public const string GroupNotFound = "group not found";

    [WolverineGet("/groups/{group}/settlement-plan", Name = "ViewSettlementPlan")]
    public static async Task<IResult> Get(string group, ClaimsPrincipal user, IQuerySession session, CancellationToken ct)
    {
        // A malformed id is answered like a missing group: nothing is disclosed either way.
        var state = GroupId.TryParse(group, out var groupId)
            ? await session.Events.AggregateStreamAsync<State>(groupId.Value, token: ct)
            : null;

        return Reader.Read(state, new Query(user.UserId())) is { } plan
            ? Results.Ok(plan)
            : Results.Problem(GroupNotFound, statusCode: StatusCodes.Status404NotFound);
    }
}
