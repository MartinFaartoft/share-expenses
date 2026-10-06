using System.Security.Claims;
using JasperFx;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SplitIt.Infrastructure.Identity;
using SplitIt.Infrastructure.Invites;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using Wolverine.Http;
using Wolverine.Marten;

namespace SplitIt.Slices.AcceptInvite;

/// <summary>
/// <c>POST /invites/{group}/join</c> — "Join" on one of the user's invites, from the
/// Home screen. No fields but the antiforgery token: the invite is found by the
/// signed-in user's address (spec §4).
///
/// Wolverine fetches the group (<c>FetchForWriting</c>), runs <see cref="Post"/>,
/// appends the claim and saves — deleting the slot's <see cref="Invite"/> in the same
/// transaction: the address is not needed once claimed.
/// </summary>
public static class Endpoint
{
    /// <summary>
    /// The stream to fetch, from the route. A malformed id becomes one no stream has,
    /// so it takes the unknown-group path: the same "invite not found" as any dead invite.
    /// </summary>
    public static GroupId GroupStream(string group) =>
        GroupId.TryParse(group, out var id) ? id : GroupId.New();

    /// <summary>
    /// Joined, or already in: into the group page. The invite gone: back home, whose
    /// invites are live, so it shows the truth.
    /// </summary>
    [ValidateAntiforgery]
    [WolverinePost("/invites/{group}/join")]
    public static async Task<(IResult, Events)> Post(
        string group,
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        ClaimsPrincipal user,
        IDocumentSession session,
        IEmailDirectory directory,
        TimeProvider clock,
        CancellationToken ct)
    {
        var groupId = GroupStream(group);
        var userId = user.UserId();

        // Lookup (spec §11): the invites in this group sent to the user's address —
        // verified, since accounts are only created by signing in with a code sent to it.
        var address = await directory.AddressOf(userId, ct);
        var invites = address is null ? [] : await Invite.GetInvitesFor(session, address, groupId, ct);
        var command = new Command(clock.GetUtcNow(), userId, invites.Select(i => i.InviteId).ToHashSet());

        switch (Decider.Decide(state, command))
        {
            case Decision.Accepted accepted:
                // The address is not needed once claimed: the slot's Invite goes in the same transaction.
                var claimed = accepted.Events.OfType<MemberClaimed>().Single().MemberId;
                session.DeleteWhere<Invite>(i => i.GroupId == groupId && i.MemberId == claimed);
                return (Results.Redirect($"/groups/{groupId}"), [.. accepted.Events]);
            case Decision.Rejected { Kind: Rejection.AlreadyMember }:
                return (Results.Redirect($"/groups/{groupId}"), []);
            case Decision.Rejected:
                return (Results.Redirect("/"), []);
            case var other:
                throw new InvalidOperationException($"Unhandled decision {other}");
        }
    }

    /// <summary>
    /// Two claims racing on one slot: the loser gets a 409, and a retry finds the invite
    /// used. Still a bare problem response, not a page: a friendly retry is the open
    /// "409 retry UX in forms" task (spec §14).
    /// </summary>
    public static ProblemDetails OnException(ConcurrencyException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "the group changed while you were joining; please try again",
    };
}
