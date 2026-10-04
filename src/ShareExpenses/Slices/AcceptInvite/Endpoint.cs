using System.Security.Claims;
using JasperFx;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Infrastructure.Invites;
using ShareExpenses.Shared;
using ShareExpenses.Slices.CreateGroup;
using Wolverine.Http;
using Wolverine.Marten;

namespace ShareExpenses.Slices.AcceptInvite;

internal sealed record Response(GroupId GroupId, MemberId MemberId);

/// <summary>
/// <c>POST /invites/{group}/accept</c> — "Join" on one of the user's invites. No body:
/// the invite is found by the signed-in user's address (spec §4).
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

    [WolverinePost("/api/invites/{group}/accept", Name = "AcceptInvite")]
    public static async Task<(IResult, Events)> Post(
        string group,
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        ClaimsPrincipal user,
        IDocumentSession session,
        IEmailDirectory directory,
        TimeProvider clock,
        CancellationToken ct)
    {
        var (groupId, command, decision) = await Decide(group, state, user, session, directory, clock, ct);
        switch (decision)
        {
            case Decision.Accepted accepted:
                return (Results.Ok(new Response(groupId, Claim(session, groupId, accepted))), [.. accepted.Events]);
            case Decision.Rejected { Kind: Rejection.AlreadyMember } rejected:
                // The user already holds a slot; the client can go straight there.
                return (Results.Problem(
                    rejected.Reason, statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    {
                        ["groupId"] = groupId,
                        ["memberId"] = state!.Members[command.UserId],
                    }), []);
            case Decision.Rejected rejected:
                return (Results.Problem(rejected.Reason, statusCode: StatusCodes.Status404NotFound), []);
            case var other:
                throw new InvalidOperationException($"Unhandled decision {other}");
        }
    }

    /// <summary>
    /// <c>POST /invites/{group}/join</c> — "Join" on the Home screen: the same decision,
    /// answered as a browser needs it. Joined, or already in: into the group page. The
    /// invite gone: back home, whose invites are live, so it shows the truth.
    /// </summary>
    [ValidateAntiforgery]
    [WolverinePost("/invites/{group}/join", Name = "JoinFromHome")]
    public static async Task<(IResult, Events)> Join(
        string group,
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        ClaimsPrincipal user,
        IDocumentSession session,
        IEmailDirectory directory,
        TimeProvider clock,
        CancellationToken ct)
    {
        var (groupId, _, decision) = await Decide(group, state, user, session, directory, clock, ct);
        return decision switch
        {
            Decision.Accepted accepted =>
                (Claimed(session, groupId, accepted), [.. accepted.Events]),
            Decision.Rejected { Kind: Rejection.AlreadyMember } => (Results.Redirect($"/groups/{groupId}"), []),
            Decision.Rejected => (Results.Redirect("/"), []),
            var other => throw new InvalidOperationException($"Unhandled decision {other}"),
        };
    }

    private static IResult Claimed(IDocumentSession session, GroupId groupId, Decision.Accepted accepted)
    {
        Claim(session, groupId, accepted);
        return Results.Redirect($"/groups/{groupId}");
    }

    private static async Task<(GroupId, Command, Decision)> Decide(
        string group, State? state, ClaimsPrincipal user, IDocumentSession session, IEmailDirectory directory,
        TimeProvider clock, CancellationToken ct)
    {
        var groupId = GroupStream(group);
        var userId = user.UserId();

        // Lookup (spec §11): the invites in this group sent to the user's address —
        // verified, since accounts are only created by signing in with a code sent to it.
        var address = await directory.AddressOf(userId, ct);
        var invites = address is null ? [] : await Invite.AddressedTo(session, address, groupId, ct);
        var command = new Command(clock.GetUtcNow(), userId, invites.Select(i => i.InviteId).ToHashSet());
        return (groupId, command, Decider.Decide(state, command));
    }

    /// <summary>
    /// The claimed slot; its <see cref="Invite"/> is deleted in the same transaction —
    /// the address is not needed once claimed.
    /// </summary>
    private static MemberId Claim(IDocumentSession session, GroupId groupId, Decision.Accepted accepted)
    {
        var claimed = accepted.Events.OfType<MemberClaimed>().Single().MemberId;
        session.DeleteWhere<Invite>(i => i.GroupId == groupId && i.MemberId == claimed);
        return claimed;
    }

    /// <summary>
    /// Client retries on 409, as in AddMember. Two claims racing on one slot: the
    /// retry finds the invite used, and answers "not found".
    /// </summary>
    public static ProblemDetails OnException(ConcurrencyException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "the group changed while you were joining; please try again",
    };
}
