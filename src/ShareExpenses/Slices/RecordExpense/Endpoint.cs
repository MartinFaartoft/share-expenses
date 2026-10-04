using System.Security.Claims;
using JasperFx;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;
using Wolverine.Http;
using Wolverine.Marten;

namespace ShareExpenses.Slices.RecordExpense;

// Public: Wolverine generates code against the endpoint's signature (spec §12).
/// <param name="AmountMinor">In the group currency's minor unit; the client converts what the user typed.</param>
/// <param name="Split">One shape per mode (spec §7); a malformed one does not read, and is answered 400.</param>
public sealed record Request(
    string? Description,
    long AmountMinor,
    MemberId? PayerMemberId,
    ExpenseSplit? Split,
    DateOnly? PaidOn);

internal sealed record Response(ExpenseId ExpenseId);

/// <summary>
/// <c>POST /groups/{group}/expenses</c> — the Add expense screen's submit.
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

    [WolverinePost("/api/groups/{group}/expenses", Name = "RecordExpense")]
    public static (IResult, Events) Post(
        Request request,
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        ClaimsPrincipal user,
        TimeProvider clock,
        HttpContext http)
    {
        var command = new Command(
            ExpenseId.New(), request.Description, request.AmountMinor, request.PayerMemberId, request.Split,
            request.PaidOn, clock.GetUtcNow(), user.UserId());

        return Decider.Decide(state, command) switch
        {
            Decision.Accepted accepted => (
                Results.Created($"{http.Request.Path}/{command.ExpenseId}", new Response(command.ExpenseId)),
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
        Detail = "the group changed while you were adding the expense; please try again",
    };
}
