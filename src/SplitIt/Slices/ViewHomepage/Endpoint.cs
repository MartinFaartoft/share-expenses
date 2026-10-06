using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using SplitIt.Infrastructure.Identity;
using SplitIt.Infrastructure.Invites;
using SplitIt.Shared;
using Wolverine.Http;

namespace SplitIt.Slices.ViewHomepage;

public static class Endpoint
{
    [WolverineGet("/")]
    public static async Task<IResult> Get(
        ClaimsPrincipal user, IQuerySession session, IEmailDirectory directory, TimeProvider clock, CancellationToken ct) =>
        new RazorComponentResult<HomePage>(new { Model = await Read(user, session, directory, clock, ct) });

    private static async Task<HomepageReadModel> Read(
        ClaimsPrincipal user, IQuerySession session, IEmailDirectory directory, TimeProvider clock, CancellationToken ct)
    {
        var userId = user.UserId();
        
        var userEmail = await directory.AddressOf(userId, ct);
        var invites = userEmail is null ? [] : await Invite.GetInvitesFor(session, userEmail, group: null, ct);

        var invited = new Dictionary<GroupId, State?>();
        foreach (var group in invites.Select(i => i.GroupId).Distinct())
            invited[group] = await session.Events.AggregateStreamAsync<State>(group.Value, token: ct);

        var memberships = await session.Query<Membership>().Where(m => ((IEnumerable<UserId>)m.Members).Contains(userId)).ToListAsync(ct);

        var query = new Query(userId, invites.Select(i => (i.GroupId, i.InviteId)).ToList(), clock.GetUtcNow());
        return Reader.Read(query, invited, memberships);
    }
}
