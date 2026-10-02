using System.Security.Claims;
using JasperFx;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShareExpenses.Infrastructure;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Infrastructure.Invites;
using ShareExpenses.Shared;
using Wolverine.Http;
using Wolverine.Marten;

namespace ShareExpenses.Slices.InviteMember;

// Public: Wolverine generates code against the endpoint's signature (spec §12).
public sealed record Request(string? Email);

/// <summary>
/// The invite email, carried from the endpoint (which knows what to say, but runs
/// before the save) to <see cref="Endpoint.AfterCommitAsync"/> (which runs only once
/// the invite is saved). Empty unless the invite was accepted.
/// </summary>
public sealed class PendingEmail
{
    internal InviteEmail? Email { get; set; }
}

/// <param name="AppLink">Where to sign in. Carries no secret: only the invited address can claim (spec §4).</param>
internal sealed record InviteEmail(
    GroupId GroupId, MemberId MemberId, string To, string AppLink, string GroupName, string InviterName, string MemberName);

/// <summary>
/// <c>POST /groups/{group}/members/{memberId}/invite</c> — the Invite member screen's submit.
///
/// Wolverine fetches the group (<c>FetchForWriting</c>), runs <see cref="Post"/>,
/// appends the returned events, saves — the event and the <see cref="Invite"/>
/// document in one transaction — and only then runs <see cref="AfterCommitAsync"/>.
/// </summary>
public static class Endpoint
{
    /// <summary>
    /// The stream to fetch, from the route. A malformed id becomes one no stream has,
    /// so it takes the unknown-group path: same answer, same work.
    /// </summary>
    public static GroupId GroupStream(string group) =>
        GroupId.TryParse(group, out var id) ? id : GroupId.New();

    /// <summary>Runs before <see cref="Post"/>; Wolverine passes the result to both it and the after-commit step.</summary>
    public static PendingEmail Load() => new();

    // memberId is bound as a string: a malformed one still goes through deciding — as
    // an id no slot has — so membership is checked first and a non-member still only
    // ever sees "group not found".
    [WolverinePost("/groups/{group}/members/{memberId}/invite", Name = "InviteMember")]
    [Authorize]
    public static async Task<(IResult, Events)> Post(
        string group,
        string memberId,
        Request request,
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        ClaimsPrincipal user,
        IDocumentSession session,
        IEmailDirectory directory,
        PublicOrigin origin,
        TimeProvider clock,
        HttpContext http,
        PendingEmail pending,
        CancellationToken ct)
    {
        var groupId = GroupStream(group);
        var member = MemberId.TryParse(memberId, out var parsed) ? parsed : MemberId.From(Guid.Empty);
        var email = EmailAddress.Trim(request.Email);

        // Lookups for the guards (spec §11): outside the stream, possibly stale.
        var emailHolder = email.Length > 0 ? await directory.AccountFor(email, ct) : null;
        var invitedTo = (await Invite.AddressedTo(session, email, groupId, ct)).Select(i => i.MemberId).ToHashSet();

        var command = new Command(
            member, request.Email, InviteId.New(), clock.GetUtcNow(), user.UserId(),
            EmailHolder: emailHolder, InvitedTo: invitedTo);

        switch (Decider.Decide(state, command))
        {
            case Decision.Accepted accepted:
                // Same session, same transaction as the event: one Invite per slot, so
                // re-inviting replaces the slot's previous one.
                session.DeleteWhere<Invite>(i => i.GroupId == groupId && i.MemberId == command.MemberId);
                session.Store(new Invite
                {
                    Id = command.InviteId.Value,
                    GroupId = groupId,
                    MemberId = command.MemberId,
                    Email = email,
                    NormalizedEmail = EmailAddress.Normalize(email),
                });

                // Accepted implies the actor is a member and the slot exists.
                pending.Email = new InviteEmail(
                    groupId, command.MemberId, email, $"{origin.For(http.Request)}/",
                    state!.GroupName, state.Slots[state.Members[command.By]].Name, state.Slots[command.MemberId].Name);
                return (Results.NoContent(), [.. accepted.Events]);
            case Decision.Rejected { Kind: Rejection.NotFound } rejected:
                return (Results.Problem(rejected.Reason, statusCode: StatusCodes.Status404NotFound), []);
            case Decision.Rejected rejected:
                return (Results.Problem(rejected.Reason, statusCode: StatusCodes.Status400BadRequest), []);
            case var other:
                throw new InvalidOperationException($"Unhandled decision {other}");
        }
    }

    /// <summary>
    /// After the save, and only if it succeeded: the invite has happened whether or
    /// not the email arrives — the invitee can sign in with the address regardless.
    /// A failure is logged, not returned.
    /// </summary>
    public static async Task AfterCommitAsync(
        PendingEmail pending, IEmailSender sender, ILogger<PendingEmail> logger, CancellationToken ct)
    {
        if (pending.Email is not { } email)
            return;

        try
        {
            await sender.SendInviteAsync(email.To, email.AppLink, email.GroupName, email.InviterName, email.MemberName, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Ids, not the address: personal data stays out of logs.
            logger.LogError(e,
                "Invite to group {GroupId} for member {MemberId} was recorded, but its email could not be sent",
                email.GroupId, email.MemberId);
        }
    }

    /// <summary>Client retries on 409, as in AddMember — see the decision recorded there.</summary>
    public static ProblemDetails OnException(ConcurrencyException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "the group changed while you were inviting; please try again",
    };
}
