using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using SplitIt.Infrastructure.Identity;
using SplitIt.Shared;
using SplitIt.Web;
using Wolverine.Http;

namespace SplitIt.Slices.ViewGroup;

public static class Endpoint
{
    [WolverineGet("/groups/{group}")]
    public static async Task<IResult> Get(string group, ClaimsPrincipal user, IQuerySession session, CancellationToken ct)
    {
        GroupActivityReadModel? readModel = await Read(group, user, session, ct);
        return readModel  is { } rm
            ? new RazorComponentResult<GroupPage>(new { ReadModel = rm })
            // A non-member, a missing group and a malformed id: the same page, the same 404.
            : new RazorComponentResult<NotFoundPage>(new { What = "group" })
                { StatusCode = StatusCodes.Status404NotFound };
    }

    private static async Task<GroupActivityReadModel?> Read(
        string group, ClaimsPrincipal user, IQuerySession session, CancellationToken ct)
    {
        // A malformed id is answered like a missing group: nothing is disclosed either way.
        var state = GroupId.TryParse(group, out var groupId) ? await session.LoadAsync<State>(groupId.Value, ct) : null;
        return Reader.Read(state, new Query(user.UserId()));
    }
}
