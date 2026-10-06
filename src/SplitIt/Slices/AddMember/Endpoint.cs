using System.Security.Claims;
using JasperFx;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SplitIt.Infrastructure;
using SplitIt.Infrastructure.Identity;
using SplitIt.Infrastructure.Invites;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Web;
using Wolverine.Http;
using Wolverine.Marten;

namespace SplitIt.Slices.AddMember;

/// <summary>
/// The invite email, carried from the endpoint (which knows what to say, but runs
/// before the save) to <see cref="Endpoint.AfterCommitAsync"/> (which runs only once
/// the invite is saved). Empty unless a member was invited.
/// </summary>
public sealed class PendingEmail
{
    internal InviteEmail? Email { get; set; }
}

/// <param name="AppLink">Where to sign in. Carries no secret: only the invited address can claim (spec §4).</param>
internal sealed record InviteEmail(
    GroupId GroupId, MemberId MemberId, string To, string AppLink, string GroupName, string InviterName, string MemberName);

/// <summary>
/// The Add member screen: <c>GET /groups/{group}/members/new</c>, the people and the
/// form; <c>POST /groups/{group}/members</c>, its submit. A plain form, no htmx
/// (slice-02-add-member.md).
/// </summary>
public static class Endpoint
{
    public static GroupId GroupStream(string group) =>
        GroupId.TryParse(group, out var id) ? id : GroupId.New();

    /// <summary>Runs before <see cref="Post"/>; Wolverine passes the result to both it and the after-commit step.</summary>
    public static PendingEmail Load() => new();

    [WolverineGet("/groups/{group}/members/new")]
    public static async Task<IResult> Get(
        string group, ClaimsPrincipal user, IQuerySession session, TimeProvider clock, CancellationToken ct)
    {
        var groupId = GroupStream(group);
        var state = GroupId.TryParse(group, out _)
            ? await session.Events.AggregateStreamAsync<State>(groupId.Value, token: ct)
            : null;
        var userId = user.UserId();

        return state is not null && state.Members.ContainsKey(userId)
            ? Page(AddMemberForm.Blank(state, groupId, MemberId.New(), userId, clock.GetUtcNow()))
            : NotFound();
    }

    /// <summary>
    /// Added, or already added by this very form: the screen again, the new person in
    /// the list. Rejected: the screen again, with what was entered, and why.
    /// </summary>
    [ValidateAntiforgery]
    [WolverinePost("/groups/{group}/members")]
    public static async Task<(IResult, Events)> Post(
        string group,
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        HttpRequest request,
        ClaimsPrincipal user,
        IDocumentSession session,
        IEmailDirectory directory,
        [FromServices] PublicOrigin origin,
        TimeProvider clock,
        [NotBody] PendingEmail pending,
        CancellationToken ct)
    {
        var groupId = GroupStream(group);
        var userId = user.UserId();
        var posted = await request.ReadFormAsync(ct);

        var memberId = MemberId.TryParse(posted["memberId"], out var parsed) ? parsed : MemberId.New();
        var email = EmailAddress.Trim(posted["email"]);

        // Lookups for the invite guards (spec §11): outside the stream, possibly stale,
        // and only when there is an address to invite.
        UserId? emailHolder = null;
        HashSet<MemberId>? invitedTo = null;
        if (email.Length > 0)
        {
            emailHolder = await directory.AccountFor(email, ct);
            invitedTo = (await Invite.GetInvitesFor(session, email, groupId, ct)).Select(i => i.MemberId).ToHashSet();
        }

        var command = new Command(
            memberId, posted["displayName"], email, InviteId.New(), clock.GetUtcNow(), userId,
            EmailHolder: emailHolder, InvitedTo: invitedTo);

        switch (Decider.Decide(state, command))
        {
            case Decision.Accepted accepted:
                if (accepted.Events.OfType<MemberInvited>().SingleOrDefault() is { } invited)
                {
                    // Same session, same transaction as the events.
                    session.Store(new Invite
                    {
                        Id = invited.InviteId.Value,
                        GroupId = groupId,
                        MemberId = invited.MemberId,
                        Email = email,
                        NormalizedEmail = EmailAddress.Normalize(email),
                    });
                    pending.Email = new InviteEmail(
                        groupId, invited.MemberId, email, $"{origin.For(request)}/", state!.GroupName,
                        state.Slots[state.Members[userId]].Name,
                        accepted.Events.OfType<MemberAdded>().Single().DisplayName);
                }
                return (Results.Redirect($"/groups/{groupId}/members/new"), [.. accepted.Events]);
            case Decision.Rejected { Kind: Rejection.AlreadyRecorded }:
                return (Results.Redirect($"/groups/{groupId}/members/new"), []);
            case Decision.Rejected { Kind: Rejection.NotFound }:
                return (NotFound(), []);
            case Decision.Rejected rejected:
                var entered = AddMemberForm.Blank(state!, groupId, memberId, userId, clock.GetUtcNow()) with
                {
                    DisplayName = posted["displayName"].ToString(),
                    Email = posted["email"].ToString(),
                    Error = rejected.Reason,
                };
                return (Page(entered), []);
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

    /// <summary>Two saves to the group at the same instant: the loser gets a 409 (slice-02-add-member.md).</summary>
    public static ProblemDetails OnException(ConcurrencyException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "the group changed while you were saving; please try again",
    };

    private static IResult Page(AddMemberForm form) => new RazorComponentResult<AddMemberPage>(new { Form = form });

    private static IResult NotFound() =>
        new RazorComponentResult<NotFoundPage>(new { What = "group" }) { StatusCode = StatusCodes.Status404NotFound };
}
