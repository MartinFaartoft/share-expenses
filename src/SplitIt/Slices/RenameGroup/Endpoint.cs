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

namespace SplitIt.Slices.RenameGroup;

/// <summary>
/// What the Rename group form shows: the name as typed — the group's current name at first,
/// as submitted after a rejection, with its reason. Public: the screen takes it as a
/// component parameter (spec §3).
/// </summary>
public sealed record RenameForm(GroupId GroupId, string Name, string? Error);

/// <summary>
/// The Rename group screen: <c>GET /groups/{group}/rename</c>, the form; <c>POST</c> to the
/// same path, its submit. A plain form, no htmx (slice-14-rename-group.md).
/// </summary>
public static class Endpoint
{
    public static GroupId GroupStream(string group) =>
        GroupId.TryParse(group, out var id) ? id : GroupId.New();

    [WolverineGet("/groups/{group}/rename")]
    public static async Task<IResult> Get(
        string group, ClaimsPrincipal user, IQuerySession session, CancellationToken ct)
    {
        var groupId = GroupStream(group);
        var state = GroupId.TryParse(group, out _)
            ? await session.Events.AggregateStreamAsync<State>(groupId.Value, token: ct)
            : null;

        return state is not null && state.Members.ContainsKey(user.UserId())
            ? Page(new RenameForm(groupId, state.GroupName, Error: null))
            : NotFound();
    }

    /// <summary>
    /// Renamed, or nothing to rename: back to the group page. Rejected: the form again, with
    /// what was typed, and why.
    /// </summary>
    [ValidateAntiforgery]
    [WolverinePost("/groups/{group}/rename")]
    public static async Task<(IResult, Events)> Post(
        string group,
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        HttpRequest request,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var groupId = GroupStream(group);
        var posted = await request.ReadFormAsync(ct);

        switch (Decider.Decide(state, new Command(posted["name"], user.UserId())))
        {
            case Decision.Accepted accepted:
                return (Results.Redirect($"/groups/{groupId}"), [.. accepted.Events]);
            case Decision.Rejected { Kind: Rejection.Unchanged }:
                return (Results.Redirect($"/groups/{groupId}"), []);
            case Decision.Rejected { Kind: Rejection.NotFound }:
                return (NotFound(), []);
            case Decision.Rejected rejected:
                return (Page(new RenameForm(groupId, posted["name"].ToString(), rejected.Reason)), []);
            case var other:
                throw new InvalidOperationException($"Unhandled decision {other}");
        }
    }

    /// <summary>Two saves to the group at the same instant: the loser gets a 409 (slice-14-rename-group.md).</summary>
    public static ProblemDetails OnException(ConcurrencyException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "the group changed while you were saving; please try again",
    };

    private static IResult Page(RenameForm form) => new RazorComponentResult<RenamePage>(new { Form = form });

    private static IResult NotFound() =>
        new RazorComponentResult<NotFoundPage>(new { What = "group" }) { StatusCode = StatusCodes.Status404NotFound };
}
