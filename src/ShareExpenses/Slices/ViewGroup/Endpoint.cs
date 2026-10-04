using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;
using ShareExpenses.Web;
using Wolverine.Http;

namespace ShareExpenses.Slices.ViewGroup;

/// <summary>
/// The group page: <c>GET /groups/{group}</c>, the screen; <c>GET /api/groups/{group}</c>,
/// the same read model as JSON.
///
/// Loads the stored <c>group_activity</c> document, projected inline with the events
/// (spec §11), and shapes it for the caller. Nothing is folded here.
/// </summary>
public static class Endpoint
{
    public const string GroupNotFound = "group not found";

    [WolverineGet("/api/groups/{group}", Name = "ViewGroup")]
    public static async Task<IResult> Get(
        string group, ClaimsPrincipal user, IQuerySession session, CancellationToken ct) =>
        await Read(group, user, session, ct) is { } activity
            ? Results.Ok(activity)
            : Results.Problem(GroupNotFound, statusCode: StatusCodes.Status404NotFound);

    [WolverineGet("/groups/{group}", Name = "GroupPage")]
    public static async Task<IResult> Page(
        string group, ClaimsPrincipal user, IQuerySession session, CancellationToken ct) =>
        await Read(group, user, session, ct) is { } activity
            ? new RazorComponentResult<GroupPage>(new { Model = activity })
            // A non-member, a missing group and a malformed id: the same page, the same 404.
            : new RazorComponentResult<NotFoundPage>(new { What = "group" }) { StatusCode = StatusCodes.Status404NotFound };

    private static async Task<GroupActivityReadModel?> Read(
        string group, ClaimsPrincipal user, IQuerySession session, CancellationToken ct)
    {
        // A malformed id is answered like a missing group: nothing is disclosed either way.
        var state = GroupId.TryParse(group, out var groupId) ? await session.LoadAsync<State>(groupId.Value, ct) : null;
        return Reader.Read(state, new Query(user.UserId()));
    }
}
