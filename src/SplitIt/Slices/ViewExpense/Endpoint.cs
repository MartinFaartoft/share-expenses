using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using SplitIt.Infrastructure.Identity;
using SplitIt.Shared;
using SplitIt.Web;
using Wolverine.Http;

namespace SplitIt.Slices.ViewExpense;

/// <summary>
/// <c>GET /groups/{group}/expenses/{expense}</c>, the Expense screen: a bottom sheet for
/// htmx, a page for anything else (slice-13-view-expense.md).
///
/// Folds the group stream live per request: nothing stored that could go stale (spec §11).
/// </summary>
public static class Endpoint
{
    [WolverineGet("/groups/{group}/expenses/{expense}")]
    public static async Task<IResult> Get(
        string group, string expense, HttpContext http, ClaimsPrincipal user, IQuerySession session, CancellationToken ct)
    {
        // The same address answers a sheet and a page: no cache may swap one for the other.
        http.Response.Headers.Append("Vary", "HX-Request");
        var sheet = http.Request.IsHtmx();

        // A malformed id is answered like a missing one: nothing is disclosed either way.
        var state = GroupId.TryParse(group, out var groupId)
            ? await session.Events.AggregateStreamAsync<State>(groupId.Value, token: ct)
            : null;
        var model = ExpenseId.TryParse(expense, out var expenseId)
            ? Reader.Read(state, new Query(expenseId, user.UserId()))
            : null;

        return (model, sheet) switch
        {
            ({ } found, true) => new RazorComponentResult<ExpenseSheet>(new { Model = found }),
            ({ } found, false) => new RazorComponentResult<ExpensePage>(new { Model = found }),
            (null, true) => new RazorComponentResult<NotFoundSheet>() { StatusCode = StatusCodes.Status404NotFound },
            (null, false) => new RazorComponentResult<NotFoundPage>(new { What = "expense" }) { StatusCode = StatusCodes.Status404NotFound },
        };
    }
}
