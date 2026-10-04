using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Infrastructure.Invites;
using ShareExpenses.Shared;
using Wolverine.Http;

namespace ShareExpenses.Slices.ViewHomepage;

internal sealed record InvitesResponse(IReadOnlyList<PendingInvite> Invites);

/// <summary>
/// The home page: <c>GET /</c>, the Home screen; <c>GET /api/invites</c>, its invites as
/// JSON, unchanged. Both read the same <see cref="HomepageReadModel"/>.
///
/// Invites are folded live per request (spec §11): the user's <c>Invite</c> documents
/// name the groups, each group's stream decides. Groups come from the stored,
/// asynchronous <see cref="Membership"/> documents — slightly behind, harmlessly.
/// </summary>
public static class Endpoint
{
    [WolverineGet("/", Name = "Home")]
    public static async Task<IResult> Home(
        ClaimsPrincipal user, IQuerySession session, IEmailDirectory directory, TimeProvider clock, CancellationToken ct) =>
        new RazorComponentResult<HomePage>(new { Model = await Read(user, session, directory, clock, ct) });

    [WolverineGet("/api/invites", Name = "ViewInvites")]
    public static async Task<IResult> Invites(
        ClaimsPrincipal user, IQuerySession session, IEmailDirectory directory, TimeProvider clock, CancellationToken ct) =>
        Results.Ok(new InvitesResponse((await Read(user, session, directory, clock, ct)).Invites));

    private static async Task<HomepageReadModel> Read(
        ClaimsPrincipal user, IQuerySession session, IEmailDirectory directory, TimeProvider clock, CancellationToken ct)
    {
        var userId = user.UserId();

        // Lookups (spec §11): the account's address — verified, since accounts are only
        // created by signing in with a code sent to it — and the invites sent to it.
        var address = await directory.AddressOf(userId, ct);
        var invites = address is null ? [] : await Invite.AddressedTo(session, address, group: null, ct);

        var invited = new Dictionary<GroupId, State?>();
        foreach (var group in invites.Select(i => i.GroupId).Distinct())
            invited[group] = await session.Events.AggregateStreamAsync<State>(group.Value, token: ct);

        var memberships = await session.Query<Membership>().Where(m => m.Members.Contains(userId)).ToListAsync(ct);

        var query = new Query(userId, invites.Select(i => (i.GroupId, i.InviteId)).ToList(), clock.GetUtcNow());
        return Reader.Read(query, invited, memberships);
    }
}
