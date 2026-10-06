using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using SplitIt.Infrastructure.Identity;
using SplitIt.Shared;
using SplitIt.Web;
using Wolverine.Http;

namespace SplitIt.Slices.ViewSettlementPlan;

/// <summary>
/// <c>GET /groups/{group}/settle-up</c>, the Settle up screen: the plan, and a form for
/// any payment. Its forms post to Record settlement. Folds the group stream live per
/// request: a plan is never stored (spec §10, §11).
/// </summary>
public static class Endpoint
{
    /// <param name="error">Why Record settlement sent the user back, if it did.</param>
    [WolverineGet("/groups/{group}/settle-up")]
    public static async Task<IResult> Get(
        string group, string? error, ClaimsPrincipal user, IQuerySession session, TimeProvider clock, CancellationToken ct)
    {
        // A malformed id is answered like a missing group: nothing is disclosed either way.
        var state = GroupId.TryParse(group, out var groupId)
            ? await session.Events.AggregateStreamAsync<State>(groupId.Value, token: ct)
            : null;

        return Reader.Read(state, new Query(user.UserId())) is { } plan
            ? new RazorComponentResult<SettleUpPage>(new
            {
                Model = plan,
                Today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime),
                Error = error,
            })
            // A non-member, a missing group and a malformed id: the same page, the same 404.
            : new RazorComponentResult<NotFoundPage>(new { What = "group" }) { StatusCode = StatusCodes.Status404NotFound };
    }
}
