using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Authorization;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Infrastructure.Invites;
using ShareExpenses.Shared;
using Wolverine.Http;

namespace ShareExpenses.Slices.ViewInvites;

/// <summary>
/// <c>GET /invites</c> — the Your invites screen: the signed-in user's live invites.
///
/// Not Wolverine's <c>[ReadAggregate]</c>, which reads one stream: the user's
/// <c>Invite</c> documents name the groups, and each group's stream is folded live
/// (spec §11) to decide which invites are still open.
/// </summary>
public static class Endpoint
{
    [WolverineGet("/invites", Name = "ViewInvites")]
    [Authorize]
    public static async Task<IResult> Get(
        ClaimsPrincipal user, IQuerySession session, IEmailDirectory directory, TimeProvider clock, CancellationToken ct)
    {
        var userId = user.UserId();

        // Lookups (spec §11): the account's address — verified, since accounts are only
        // created by signing in with a code sent to it — and the invites sent to it.
        var address = await directory.AddressOf(userId, ct);
        var invites = address is null ? [] : await Invite.AddressedTo(session, address, group: null, ct);

        var groups = new Dictionary<GroupId, State?>();
        foreach (var group in invites.Select(i => i.GroupId).Distinct())
            groups[group] = await session.Events.AggregateStreamAsync<State>(group.Value, token: ct);

        var query = new Query(userId, invites.Select(i => (i.GroupId, i.InviteId)).ToList(), clock.GetUtcNow());
        return Results.Ok(Reader.Read(query, groups));
    }
}
