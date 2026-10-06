using System.Globalization;
using System.Security.Claims;
using JasperFx;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SplitIt.Infrastructure.Identity;
using SplitIt.Shared;
using SplitIt.Web;
using Wolverine.Http;
using Wolverine.Marten;

namespace SplitIt.Slices.RecordSettlement;

/// <summary>
/// <c>POST /groups/{group}/settlements</c>, the submit of the Settle up screen's forms
/// (a plan line's Paid, and the hand-entered payment). The screen itself belongs to View
/// settlement plan; this goes back to it (slice-09-record-settlement.md).
/// </summary>
public static class Endpoint
{
    public static GroupId GroupStream(string group) =>
        GroupId.TryParse(group, out var id) ? id : GroupId.New();

    /// <summary>
    /// Recorded, or already recorded by this very form: back to Settle up. Rejected: back
    /// there too, with why.
    /// </summary>
    [ValidateAntiforgery]
    [WolverinePost("/groups/{group}/settlements")]
    public static async Task<(IResult, Events)> Post(
        string group,
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        HttpRequest request,
        ClaimsPrincipal user,
        TimeProvider clock,
        CancellationToken ct)
    {
        var groupId = GroupStream(group);
        var posted = await request.ReadFormAsync(ct);

        var settlementId = SettlementId.TryParse(posted["settlementId"], out var parsedId) ? parsedId : SettlementId.New();
        var from = MemberId.TryParse(posted["from"], out var fromId) ? fromId : (MemberId?)null;
        var to = MemberId.TryParse(posted["to"], out var toId) ? toId : (MemberId?)null;
        // Not an amount: zero, which deciding rejects as "amount must be positive".
        var amount = state is not null && Money.TryParse(posted["amount"], state.Currency, out var minor) ? minor : 0;
        var paidOn = DateOnly.TryParseExact(posted["paidOn"], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var day) ? day : (DateOnly?)null;

        var command = new Command(settlementId, from, to, amount, paidOn, clock.GetUtcNow(), user.UserId());
        var settleUp = $"/groups/{groupId}/settle-up";
        switch (Decider.Decide(state, command))
        {
            case Decision.Accepted accepted:
                return (Results.Redirect(settleUp), [.. accepted.Events]);
            case Decision.Rejected { Kind: Rejection.AlreadyRecorded }:
                return (Results.Redirect(settleUp), []);
            case Decision.Rejected { Kind: Rejection.NotFound }:
                return (new RazorComponentResult<NotFoundPage>(new { What = "group" }) { StatusCode = StatusCodes.Status404NotFound }, []);
            case Decision.Rejected rejected:
                return (Results.Redirect($"{settleUp}?error={Uri.EscapeDataString(rejected.Reason)}"), []);
            case var other:
                throw new InvalidOperationException($"Unhandled decision {other}");
        }
    }

    /// <summary>Two saves to the group at the same instant: the loser gets a 409 (slice-09-record-settlement.md).</summary>
    public static ProblemDetails OnException(ConcurrencyException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "the group changed while you were saving; please try again",
    };
}
