using ShareExpenses.Shared;
using ShareExpenses.Slices.CreateGroup;

namespace ShareExpenses.Slices.ClaimMember;

/// <summary>The command, as in <c>event-model.yaml</c>. The slot is not on it: deciding finds it.</summary>
/// <param name="Token">From the invite link's fragment, posted in the body.</param>
/// <param name="Now">The clock, passed in so deciding stays pure and testable.</param>
/// <param name="UserId">The signed-in user, claiming.</param>
internal sealed record Command(GroupId GroupId, string? Token, DateTimeOffset Now, UserId UserId);

/// <summary>Specs: <c>docs/event-model/slice-05-claim-member.md</c>.</summary>
internal static class Decider
{
    /// <summary>The one answer for every dead link — same as ViewInvite's.</summary>
    public const string InviteNotFound = "invite not found";

    /// <param name="state">The group's state, or null if its stream does not exist.</param>
    public static Decision Decide(State? state, Command command)
    {
        if (state is null)
            return Decision.NotFound(InviteNotFound);

        // The invariant — one user, one slot per group — before the token, so no
        // link can ever give a member a second slot. Telling them is safe: they're in.
        if (state.Members.TryGetValue(command.UserId, out var held))
            return Decision.AlreadyMember($"you're already in this group as {state.SlotNames[held]}");

        // Every open invite is checked, so timing does not depend on which (if any) matches.
        MemberId? slot = null;
        foreach (var (member, invite) in state.OpenInvites)
            if (InviteToken.Matches(command.Token, invite.TokenHash) && command.Now < invite.ExpiresAt)
                slot = member;

        return slot is { } claimed
            ? Decision.Accept(new MemberClaimed(claimed, command.UserId))
            : Decision.NotFound(InviteNotFound);
    }
}
