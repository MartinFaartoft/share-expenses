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
        var split = SplitFrom(mode, posted);
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
                    Mode = split is null ? ExpenseForm.EqualMode : mode,
                    Description = posted["description"].ToString(),
                    Amount = posted["amount"].ToString(),
                    Payer = payer,
                    Participants = ParticipantsOf(posted),
                    PaidOn = posted["paidOn"].ToString(),
                };
                return (Page(entered.Rejected(split is null ? "split mode not supported" : rejected.Reason)), []);
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

    /// <summary>
    /// The split a mode's fields describe; null for a mode this form does not build. One
    /// place to add shares and exact: a <c>case</c>, reading its fields by name.
    /// </summary>
    private static ExpenseSplit? SplitFrom(string mode, IFormCollection posted) => mode switch
    {
        ExpenseForm.EqualMode => new EqualSplit([.. ParticipantsOf(posted)]),
        _ => null,
    };

    private static HashSet<MemberId> ParticipantsOf(IFormCollection posted) =>
    [
        .. posted["participants"].Select(p => MemberId.TryParse(p, out var id) ? id : (MemberId?)null).OfType<MemberId>(),
    ];

    private static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private static IResult Page(ExpenseForm form) => new RazorComponentResult<EditPage>(new { Form = form });

    private static IResult NotFound() =>
        new RazorComponentResult<NotFoundPage>(new { What = "group" }) { StatusCode = StatusCodes.Status404NotFound };
}
