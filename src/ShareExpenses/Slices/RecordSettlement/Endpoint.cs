using System.Security.Claims;
using JasperFx;
using Microsoft.AspNetCore.Mvc;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;
using Wolverine.Http;
using Wolverine.Marten;

namespace ShareExpenses.Slices.RecordSettlement;

// Public: Wolverine generates code against the endpoint's signature (spec §12).
/// <param name="AmountMinor">In the group currency's minor unit; the client converts what the user typed.</param>
public sealed record Request(MemberId? FromMemberId, MemberId? ToMemberId, long AmountMinor, DateOnly? PaidOn);

internal sealed record Response(SettlementId SettlementId);

/// <summary>
/// <c>POST /groups/{group}/settlements</c> — "Paid" on a plan line, or a payment
/// entered by hand on the Settle up screen.
///
/// Wolverine's aggregate handler workflow, as in AddMember: Wolverine fetches the
/// group, runs <see cref="Post"/>, appends the returned events and saves.
/// </summary>
public static class Endpoint
{
    /// <summary>
    /// The stream to fetch, from the route. A malformed id becomes one no stream has,
    /// so it takes the unknown-group path: same answer, same work.
    /// </summary>
    public static GroupId GroupStream(string group) =>
        GroupId.TryParse(group, out var id) ? id : GroupId.New();

    [WolverinePost("/api/groups/{group}/settlements", Name = "RecordSettlement")]
    public static (IResult, Events) Post(
        Request request,
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        ClaimsPrincipal user,
        TimeProvider clock,
        HttpContext http)
    {
        var command = new Command(
            SettlementId.New(), request.FromMemberId, request.ToMemberId, request.AmountMinor, request.PaidOn,
            clock.GetUtcNow(), user.UserId());

        return Decider.Decide(state, command) switch
        {
            Decision.Accepted accepted => (
                Results.Created($"{http.Request.Path}/{command.SettlementId}", new Response(command.SettlementId)),
                [.. accepted.Events]),
            Decision.Rejected { Kind: Rejection.NotFound } rejected => (
                Results.Problem(rejected.Reason, statusCode: StatusCodes.Status404NotFound),
                []),
            Decision.Rejected rejected => (
                Results.Problem(rejected.Reason, statusCode: StatusCodes.Status400BadRequest),
                []),
            var other => throw new InvalidOperationException($"Unhandled decision {other}"),
        };
    }

    /// <summary>Client retries on 409, as in AddMember — see the decision recorded there.</summary>
    public static ProblemDetails OnException(ConcurrencyException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "the group changed while you were recording the payment; please try again",
    };
}
