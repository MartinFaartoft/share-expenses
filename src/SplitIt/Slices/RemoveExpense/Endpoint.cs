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

namespace SplitIt.Slices.RemoveExpense;

public static class Endpoint
{
    public static GroupId GroupStream(string group) =>
        GroupId.TryParse(group, out var id) ? id : GroupId.New();

    [WolverineGet("/groups/{group}/expenses/{expense}/remove")]
    public static async Task<IResult> Get(
        string group, string expense, ClaimsPrincipal user, IQuerySession session, CancellationToken ct)
    {
        var groupId = GroupStream(group);
        var state = GroupId.TryParse(group, out _)
            ? await session.Events.AggregateStreamAsync<State>(groupId.Value, token: ct)
            : null;

        return state is not null
               && state.Members.ContainsKey(user.UserId())
               && ExpenseId.TryParse(expense, out var expenseId)
               && state.Expenses.TryGetValue(expenseId, out var recorded)
               && !state.Removed.Contains(expenseId)
            ? new RazorComponentResult<RemovePage>(new
            {
                GroupId = groupId,
                ExpenseId = expenseId,
                Currency = state.Currency,
                Expense = recorded,
                PayerName = state.Names[recorded.PayerMemberId],
                IsPayer = state.Members[user.UserId()] == recorded.PayerMemberId,
            })
            : NotFound();
    }

    [ValidateAntiforgery]
    [WolverinePost("/groups/{group}/expenses/{expense}/remove")]
    public static (IResult, Events) Post(
        string group,
        string expense,
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        ClaimsPrincipal user)
    {
        var back = Results.Redirect($"/groups/{GroupStream(group)}");
        if (!ExpenseId.TryParse(expense, out var expenseId))
            return (NotFound(), []);

        return Decider.Decide(state, new Command(expenseId, user.UserId())) switch
        {
            Decision.Accepted accepted => (back, [.. accepted.Events]),
            Decision.Rejected { Kind: Rejection.AlreadyRemoved } => (back, []),
            // An archived group: the group page says so, in a banner (slice-15-archive-group.md).
            Decision.Rejected { Kind: Rejection.Invalid } => (back, []),
            Decision.Rejected => (NotFound(), []),
            var other => throw new InvalidOperationException($"Unhandled decision {other}"),
        };
    }

    public static ProblemDetails OnException(ConcurrencyException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "the group changed while you were saving; please try again",
    };

    private static IResult NotFound() =>
        new RazorComponentResult<NotFoundPage>(new { What = "group" }) { StatusCode = StatusCodes.Status404NotFound };
}
