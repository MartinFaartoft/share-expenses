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

// Public: Wolverine generates code against the endpoint's signature (spec §12).
public sealed record Request(string? Token);

internal sealed record Response(GroupId GroupId, MemberId MemberId);

/// <summary>
/// <c>POST /invites/{group}/accept</c> — "Sign me in" on the invite landing page,
/// after signing in. The token travels in the body, never in a URL (spec §11).
///
/// Wolverine fetches the group (<c>FetchForWriting</c>), runs <see cref="Post"/>,
/// appends the claim and saves — dropping the slot's invite delivery in the same
/// transaction: the address is not needed once claimed.
/// </summary>
public static class Endpoint
{
    /// <summary>
    /// The stream to fetch, from the route. A malformed id becomes one no stream has,
    /// so it takes the unknown-group path: the same "invite not found" as any dead link.
    /// </summary>
    public static GroupId GroupStream(string group) =>
        GroupId.TryParse(group, out var id) ? id : GroupId.New();

    [WolverinePost("/invites/{group}/accept", Name = "AcceptInvite")]
    [Authorize]
    public static (IResult, Events) Post(
        string group,
        Request? request,
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        ClaimsPrincipal user,
        IDocumentSession session,
        TimeProvider clock)
    {
        var groupId = GroupStream(group);
        var command = new Command(groupId, request?.Token, clock.GetUtcNow(), user.UserId());

        switch (Decider.Decide(state, command))
        {
            case Decision.Accepted accepted:
                var claimed = accepted.Events.OfType<MemberClaimed>().Single().MemberId;
                session.Delete<InviteDelivery>(claimed.Value);
                return (Results.Ok(new Response(groupId, claimed)), [.. accepted.Events]);
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
    /// Client retries on 409, as in AddMember. Two people racing on one link: the
    /// retry finds the invite used, and answers "not found".
    /// </summary>
    public static ProblemDetails OnException(ConcurrencyException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "the group changed while you were joining; please try again",
    };
}
