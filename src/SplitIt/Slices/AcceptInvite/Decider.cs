using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;

namespace SplitIt.Slices.AcceptInvite;

/// <summary>The command, as in <c>event-model.yaml</c>. The slot is not on it: deciding finds it.</summary>
/// <param name="Now">The clock, passed in so deciding stays pure and testable.</param>
/// <param name="UserId">The signed-in user, claiming.</param>
/// <param name="InvitedAs">
/// Looked up: the <c>Invite</c> documents in this group addressed to the user's
/// account email. They propose; the stream decides which, if any, is a slot's
/// current open invite (spec §11).
/// </param>
internal sealed record Command(DateTimeOffset Now, UserId UserId, IReadOnlySet<InviteId> InvitedAs);

/// <summary>Specs: <c>docs/event-model/slice-05-accept-invite.md</c>.</summary>
internal static class Decider
{
    /// <summary>The one answer for every dead invite.</summary>
    public const string InviteNotFound = "invite not found";

    /// <param name="state">The group's state, or null if its stream does not exist.</param>
    public static Decision Decide(State? state, Command command)
    {
        if (state is null)
            return Decision.NotFound(InviteNotFound);

        // The invariant — one user, one slot per group — before any invite, so no
        // invite can ever give a member a second slot. Telling them is safe: they're in.
        if (state.Members.TryGetValue(command.UserId, out var held))
            return Decision.AlreadyMember($"you're already in this group as {state.SlotNames[held]}");

        // A slot's current invite, if the lookup names it and it is unexpired. Should
        // several match — a stale guard let one address be invited twice — the newest
        // invite wins: the group's most recent intention.
        var claimable = state.OpenInvites
            .Where(open => command.InvitedAs.Contains(open.Value.InviteId) && command.Now < open.Value.ExpiresAt)
            .OrderByDescending(open => open.Value.Order)
            .Select(open => (MemberId?)open.Key)
            .FirstOrDefault();

        return claimable is { } slot
            ? Decision.Accept(new MemberClaimed(slot, command.UserId))
            : Decision.NotFound(InviteNotFound);
    }
}
