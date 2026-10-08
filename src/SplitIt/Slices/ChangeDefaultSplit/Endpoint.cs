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

namespace SplitIt.Slices.ChangeDefaultSplit;

/// <summary>
/// The Default split screen: <c>GET /groups/{group}/default-split</c>, the form; <c>POST</c> to
/// the same path, its submit. A plain form, no htmx (slice-16-change-default-split.md).
/// </summary>
public static class Endpoint
{
    public static GroupId GroupStream(string group) =>
        GroupId.TryParse(group, out var id) ? id : GroupId.New();

    [WolverineGet("/groups/{group}/default-split")]
    public static async Task<IResult> Get(
        string group, ClaimsPrincipal user, IQuerySession session, CancellationToken ct)
    {
        var groupId = GroupStream(group);
        var state = GroupId.TryParse(group, out _)
            ? await session.Events.AggregateStreamAsync<State>(groupId.Value, token: ct)
            : null;

        return state is not null && state.Members.ContainsKey(user.UserId())
            ? Page(DefaultSplitForm.Of(state, groupId, user.UserId()))
            : NotFound();
    }

    /// <summary>
    /// Saved, or nothing to save: back to the group page. Rejected: the form again, with what
    /// was entered, and why.
    /// </summary>
    [ValidateAntiforgery]
    [WolverinePost("/groups/{group}/default-split")]
    public static async Task<(IResult, Events)> Post(
        string group,
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        HttpRequest request,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var groupId = GroupStream(group);
        var userId = user.UserId();
        var posted = await request.ReadFormAsync(ct);
        var mode = posted["mode"].ToString();
        var checkedMembers = posted["participants"]
            .Select(p => MemberId.TryParse(p, out var id) ? id : (MemberId?)null).OfType<MemberId>().ToHashSet();

        var (shares, shapeError) = state is null ? ([], null) : SharesFrom(mode, state, checkedMembers, posted);

        // A share that cannot be read is not a default at all, and an empty list is a valid one:
        // answer it here, never deciding — but only to a member, who may be told what is wrong.
        if (shapeError is not null && state!.Members.ContainsKey(userId))
            return (Page(Entered(state, groupId, userId, mode, checkedMembers, posted).Rejected(shapeError)), []);

        switch (Decider.Decide(state, new Command(mode, shares, userId)))
        {
            case Decision.Accepted accepted:
                return (Results.Redirect($"/groups/{groupId}"), [.. accepted.Events]);
            case Decision.Rejected { Kind: Rejection.Unchanged }:
                return (Results.Redirect($"/groups/{groupId}"), []);
            case Decision.Rejected { Kind: Rejection.NotFound }:
                return (NotFound(), []);
            case Decision.Rejected rejected:
                return (Page(Entered(state!, groupId, userId, mode, checkedMembers, posted).Rejected(rejected.Reason)), []);
            case var other:
                throw new InvalidOperationException($"Unhandled decision {other}");
        }
    }

    /// <summary>The form as it was submitted: the mode, who was checked, every share as typed.</summary>
    private static DefaultSplitForm Entered(
        State state, GroupId groupId, UserId user, string mode, IReadOnlySet<MemberId> checkedMembers, IFormCollection posted) =>
        DefaultSplitForm.Of(state, groupId, user) with
        {
            Mode = DefaultSplitForm.IsMode(mode) ? mode : DefaultSplit.Equal,
            Participants = checkedMembers,
            Shares = state.Slots.ToDictionary(slot => slot, slot => posted[$"shares-{slot}"].ToString()),
        };

    /// <summary>Two saves to the group at the same instant: the loser gets a 409 (slice-16-change-default-split.md).</summary>
    public static ProblemDetails OnException(ConcurrencyException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "the group changed while you were saving; please try again",
    };

    /// <summary>
    /// A share for every member the form lists: 0 for one not checked, 1 for one checked under
    /// equal, and what was typed for one checked under shares. A share that cannot be read as a
    /// whole number is a shape error, answered before deciding; zero and negative ones are read
    /// as they are, and deciding says what they mean. An unknown mode reads nothing.
    /// </summary>
    private static (IReadOnlyList<MemberShares> Shares, string? Error) SharesFrom(
        string mode, State state, IReadOnlySet<MemberId> checkedMembers, IFormCollection posted)
    {
        if (!DefaultSplitForm.IsMode(mode))
            return ([], null);

        var shares = new List<MemberShares>();
        foreach (var slot in state.Slots)
        {
            if (!checkedMembers.Contains(slot))
                shares.Add(new MemberShares(slot, 0));
            else if (mode == DefaultSplit.Equal)
                shares.Add(new MemberShares(slot, 1));
            else if (int.TryParse(posted[$"shares-{slot}"].ToString().Trim(), NumberStyles.AllowLeadingSign,
                         CultureInfo.InvariantCulture, out var count))
                shares.Add(new MemberShares(slot, count));
            else
                return ([], "every share must be a whole number");
        }
        return (shares, null);
    }

    private static IResult Page(DefaultSplitForm form) => new RazorComponentResult<DefaultSplitPage>(new { Form = form });

    private static IResult NotFound() =>
        new RazorComponentResult<NotFoundPage>(new { What = "group" }) { StatusCode = StatusCodes.Status404NotFound };
}
