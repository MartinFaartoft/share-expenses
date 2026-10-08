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

namespace SplitIt.Slices.ArchiveGroup;

/// <summary>Who is not settled up, as the confirm page warns of them.</summary>
/// <param name="BalanceMinor">Positive is owed, negative owes (spec §9).</param>
public sealed record Owing(string Name, bool IsYou, long BalanceMinor);

/// <summary>What the Archive group page shows: the group, and who is not settled up (none: no warning).</summary>
public sealed record ArchiveView(GroupId GroupId, string GroupName, string Currency, IReadOnlyList<Owing> Owing);

/// <summary>
/// The Archive group screen: <c>GET /groups/{group}/archive</c>, the confirm page; <c>POST</c>
/// to the same path, its submit. A plain form, no htmx (slice-15-archive-group.md).
/// </summary>
public static class Endpoint
{
    public static GroupId GroupStream(string group) =>
        GroupId.TryParse(group, out var id) ? id : GroupId.New();

    [WolverineGet("/groups/{group}/archive")]
    public static async Task<IResult> Get(
        string group, ClaimsPrincipal user, IQuerySession session, CancellationToken ct)
    {
        var groupId = GroupStream(group);
        var state = GroupId.TryParse(group, out _)
            ? await session.Events.AggregateStreamAsync<State>(groupId.Value, token: ct)
            : null;

        if (state is null || !state.Members.TryGetValue(user.UserId(), out var you))
            return NotFound();

        // Already archived: nothing to confirm, and the group page says so.
        return state.Archived
            ? Results.Redirect($"/groups/{groupId}")
            : new RazorComponentResult<ArchivePage>(new { Model = ViewOf(state, groupId, you) });
    }

    /// <summary>Archived, or already archived: back to the group page, where the banner is the answer.</summary>
    [ValidateAntiforgery]
    [WolverinePost("/groups/{group}/archive")]
    public static (IResult, Events) Post(
        string group,
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        ClaimsPrincipal user)
    {
        var back = Results.Redirect($"/groups/{GroupStream(group)}");

        return Decider.Decide(state, new Command(user.UserId())) switch
        {
            Decision.Accepted accepted => (back, [.. accepted.Events]),
            Decision.Rejected { Kind: Rejection.Unchanged } => (back, []),
            Decision.Rejected { Kind: Rejection.NotFound } => (NotFound(), []),
            var other => throw new InvalidOperationException($"Unhandled decision {other}"),
        };
    }

    /// <summary>Two saves to the group at the same instant: the loser gets a 409 (slice-15-archive-group.md).</summary>
    public static ProblemDetails OnException(ConcurrencyException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "the group changed while you were saving; please try again",
    };

    private static ArchiveView ViewOf(State state, GroupId groupId, MemberId you) =>
        new(groupId, state.GroupName, state.Currency,
            [.. state.Slots.Where(s => s.BalanceMinor != 0).Select(s => new Owing(s.Name, s.MemberId == you, s.BalanceMinor))]);

    private static IResult NotFound() =>
        new RazorComponentResult<NotFoundPage>(new { What = "group" }) { StatusCode = StatusCodes.Status404NotFound };
}
