using System.Globalization;
using System.Security.Claims;
using JasperFx;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SplitIt.Infrastructure.Identity;
using SplitIt.Shared;
using SplitIt.Web;
using Wolverine.Http;
using Wolverine.Marten;

namespace SplitIt.Slices.EditExpense;

/// <summary>
/// The Edit expense screen: <c>GET /groups/{group}/expenses/{expense}/edit</c>, the form;
/// <c>POST</c> to the same path, its submit. A plain form, no htmx (slice-12-edit-expense.md).
/// </summary>
public static class Endpoint
{
    public static GroupId GroupStream(string group) =>
        GroupId.TryParse(group, out var id) ? id : GroupId.New();

    [WolverineGet("/groups/{group}/expenses/{expense}/edit")]
    public static async Task<IResult> Get(
        string group, string expense, ClaimsPrincipal user, IQuerySession session, TimeProvider clock, CancellationToken ct)
    {
        var groupId = GroupStream(group);
        var state = GroupId.TryParse(group, out _)
            ? await session.Events.AggregateStreamAsync<State>(groupId.Value, token: ct)
            : null;
        var userId = user.UserId();

        return state is not null
               && state.Members.ContainsKey(userId)
               && ExpenseId.TryParse(expense, out var expenseId)
               && state.Expenses.TryGetValue(expenseId, out var current)
            ? Page(ExpenseForm.Of(state, groupId, expenseId, current, userId, Today(clock)))
            : NotFound();
    }

    /// <summary>
    /// Saved, or nothing to save: back to the group page. Rejected: the form again, with
    /// what was entered, and why.
    /// </summary>
    [ValidateAntiforgery]
    [WolverinePost("/groups/{group}/expenses/{expense}/edit")]
    public static async Task<(IResult, Events)> Post(
        string group,
        string expense,
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        HttpRequest request,
        ClaimsPrincipal user,
        TimeProvider clock,
        CancellationToken ct)
    {
        var groupId = GroupStream(group);
        var userId = user.UserId();
        if (!ExpenseId.TryParse(expense, out var expenseId))
            return (NotFound(), []);

        var posted = await request.ReadFormAsync(ct);
        var mode = posted["mode"].ToString();
        var read = state is null ? new SplitRead(null, null) : SplitFrom(mode, posted, state.Currency);
        var split = read.Split;
        var amount = state is not null && Money.TryParse(posted["amount"], state.Currency, out var minor) ? minor : (long?)null;
        var payer = MemberId.TryParse(posted["payer"], out var payerId) ? payerId : (MemberId?)null;
        var paidOn = DateOnly.TryParseExact(posted["paidOn"], ExpenseForm.DateFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var day) ? day : (DateOnly?)null;

        var command = new Command(expenseId, posted["description"], amount, payer, split, paidOn, clock.GetUtcNow(), userId);
        switch (Decider.Decide(state, command))
        {
            case Decision.Accepted accepted:
                return (Results.Redirect($"/groups/{groupId}"), [.. accepted.Events]);
            case Decision.Rejected { Kind: Rejection.Unchanged }:
                return (Results.Redirect($"/groups/{groupId}"), []);
            case Decision.Rejected { Kind: Rejection.NotFound }:
                return (NotFound(), []);
            case Decision.Rejected rejected:
                var entered = ExpenseForm.Of(state!, groupId, expenseId, state!.Expenses[expenseId], userId, Today(clock)) with
                {
                    Mode = ExpenseForm.IsMode(mode) ? mode : ExpenseForm.EqualMode,
                    Description = posted["description"].ToString(),
                    Amount = posted["amount"].ToString(),
                    Payer = payer,
                    Participants = ParticipantsOf(posted),
                    Shares = state.Slots.ToDictionary(slot => slot, slot => posted[$"shares-{slot}"].ToString()),
                    Amounts = state.Slots.ToDictionary(slot => slot, slot => posted[$"amount-{slot}"].ToString()),
                    PaidOn = posted["paidOn"].ToString(),
                };
                return (Page(entered.Rejected(read.Error ?? rejected.Reason)), []);
            case var other:
                throw new InvalidOperationException($"Unhandled decision {other}");
        }
    }

    /// <summary>Two saves to the group at the same instant: the loser gets a 409 (slice-12-edit-expense.md).</summary>
    public static ProblemDetails OnException(ConcurrencyException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "the group changed while you were saving; please try again",
    };

    /// <summary>A split read from the form, or why it could not be: an unknown mode, or a field that is not a number.</summary>
    private sealed record SplitRead(ExpenseSplit? Split, string? Error);

    /// <summary>
    /// The split a mode's fields describe, for the members checked. A field that cannot be
    /// read is a shape error, answered before deciding (slice-12-edit-expense.md): a
    /// share that is not a whole number, an amount that is not an amount. Zero and negative
    /// shares are read as they are; deciding rejects them.
    /// </summary>
    private static SplitRead SplitFrom(string mode, IFormCollection posted, string currency)
    {
        var members = ParticipantsOf(posted);
        switch (mode)
        {
            case ExpenseForm.EqualMode:
                return new SplitRead(new EqualSplit([.. members]), null);
            case ExpenseForm.SharesMode:
                var shares = new List<MemberShares>();
                foreach (var member in members)
                {
                    if (!int.TryParse(posted[$"shares-{member}"].ToString().Trim(), NumberStyles.AllowLeadingSign,
                            CultureInfo.InvariantCulture, out var count))
                        return new SplitRead(null, "every share must be a whole number");
                    shares.Add(new MemberShares(member, count));
                }
                return new SplitRead(new SharesSplit(shares), null);
            case ExpenseForm.ExactMode:
                var amounts = new List<MemberAmount>();
                foreach (var member in members)
                {
                    if (!Money.TryParse(posted[$"amount-{member}"], currency, out var minor))
                        return new SplitRead(null, "every exact amount must be a number");
                    amounts.Add(new MemberAmount(member, minor));
                }
                return new SplitRead(new ExactSplit(amounts), null);
            default:
                return new SplitRead(null, "split mode not supported");
        }
    }

    private static HashSet<MemberId> ParticipantsOf(IFormCollection posted) =>
    [
        .. posted["participants"].Select(p => MemberId.TryParse(p, out var id) ? id : (MemberId?)null).OfType<MemberId>(),
    ];

    private static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private static IResult Page(ExpenseForm form) => new RazorComponentResult<EditPage>(new { Form = form });

    private static IResult NotFound() =>
        new RazorComponentResult<NotFoundPage>(new { What = "group" }) { StatusCode = StatusCodes.Status404NotFound };
}
