using SplitIt.Shared;
using SplitIt.Slices.AddMember;

namespace SplitIt.Slices.InviteMember;

/// <summary>The command, as in <c>event-model.yaml</c>. Inputs are raw; deciding validates them.</summary>
/// <param name="InviteId">The new invite's id, chosen by the caller: names its <c>Invite</c> document.</param>
/// <param name="Now">The clock, read by the caller, so deciding stays pure and testable.</param>
/// <param name="EmailHolder">
/// Looked up: the user whose account email is <paramref name="Email"/>, if any.
/// </param>
/// <param name="InvitedTo">
/// Looked up: slots in this group whose <c>Invite</c> is addressed to <paramref name="Email"/>.
/// </param>
/// <remarks>
/// The two lookups feed <em>guards</em>, not invariants (spec §11): they come from
/// outside the stream and may be stale. The worst a stale lookup can do is let a
/// mistake through to claim time, where "one user, one slot" is enforced against
/// the stream itself.
///
/// No group id: it only selects the stream, which Wolverine fetches from the route
/// before the endpoint runs (spec §13). Deciding gets the group as its folded state.
/// </remarks>
internal sealed record Command(
    MemberId MemberId,
    string? Email,
    InviteId InviteId,
    DateTimeOffset Now,
    UserId By,
    UserId? EmailHolder = null,
    IReadOnlySet<MemberId>? InvitedTo = null);

/// <summary>Specs: <c>docs/event-model/slice-03-invite-member.md</c>.</summary>
internal static class Decider
{
    /// <summary>
    /// The single answer for "no such group" and "not a member", so a non-member
    /// cannot probe which groups exist.
    /// </summary>
    public const string GroupNotFound = "group not found";

    public const string MemberNotFound = "member not found";

    /// <param name="state">The group's state, or null if its stream does not exist.</param>
    public static Decision Decide(State? state, Command command)
    {
        // Membership first: a non-member learns nothing, not even from validation.
        if (state is null || !state.Members.ContainsKey(command.By))
            return Decision.NotFound(GroupNotFound);

        if (!state.Slots.TryGetValue(command.MemberId, out var slot))
            return Decision.NotFound(MemberNotFound);
        if (slot.Claimed)
            return Decision.Reject("member has already joined");

        var email = EmailAddress.Trim(command.Email);
        if (Invitations.Refusal(email, command.EmailHolder, command.InvitedTo, command.MemberId,
                joinedAs: user => state.Members.TryGetValue(user, out var held) ? state.Slots[held].Name : null,
                openSlotName: slot => state.Slots.GetValueOrDefault(slot) is { Claimed: false } open ? open.Name : null)
            is { } refusal)
            return Decision.Reject(refusal);

        return Decision.Accept(new MemberInvited(
            command.MemberId, command.InviteId, command.Now + Invitations.Lifetime, command.By));
    }
}
