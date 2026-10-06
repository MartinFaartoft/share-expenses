using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;

namespace SplitIt.Slices.AddMember;

/// <summary>The command, as in <c>event-model.yaml</c>. Inputs are raw; deciding validates them.</summary>
/// <param name="MemberId">The new slot's id, chosen by the caller.</param>
/// <param name="Email">Optional: given, the new member is invited at it.</param>
/// <param name="InviteId">The new invite's id, chosen by the caller: names its <c>Invite</c> document.</param>
/// <param name="Now">The clock, read by the caller, so deciding stays pure and testable.</param>
/// <param name="EmailHolder">Looked up, only with an email: the user whose account email is <paramref name="Email"/>, if any.</param>
/// <param name="InvitedTo">Looked up, only with an email: slots in this group whose <c>Invite</c> is addressed to it.</param>
internal sealed record Command(
    MemberId MemberId,
    string? DisplayName,
    string? Email,
    InviteId InviteId,
    DateTimeOffset Now,
    UserId By,
    UserId? EmailHolder = null,
    IReadOnlySet<MemberId>? InvitedTo = null);

/// <summary>Specs: <c>docs/event-model/slice-02-add-member.md</c>.</summary>
internal static class Decider
{
    /// <summary>
    /// The single answer for "no such group" and "not a member", so a non-member
    /// cannot probe which groups exist.
    /// </summary>
    public const string GroupNotFound = "group not found";

    /// <param name="state">The group's state, or null if its stream does not exist.</param>
    public static Decision Decide(State? state, Command command)
    {
        // Membership first: a non-member learns nothing, not even from validation.
        if (state is null || !state.Members.ContainsKey(command.By))
            return Decision.NotFound(GroupNotFound);

        if (state.Slots.ContainsKey(command.MemberId))
            return Decision.AlreadyRecorded("member already added");

        var name = command.DisplayName?.Trim() ?? "";
        if (name.Length == 0)
            return Decision.Reject("name is required");
        if (Names.VisibleLength(name) > Names.DisplayNameMaxLength)
            return Decision.Reject($"name must be at most {Names.DisplayNameMaxLength} characters");
        if (state.NameTaken(name))
            return Decision.Reject("a member with that name already exists");

        var added = new MemberAdded(command.MemberId, name, command.By);
        var email = EmailAddress.Trim(command.Email);
        if (email.Length == 0)
            return Decision.Accept(added);

        if (Invitations.Refusal(email, command.EmailHolder, command.InvitedTo, command.MemberId,
                joinedAs: user => state.Members.TryGetValue(user, out var held) ? state.Slots[held].Name : null,
                openSlotName: slot => state.Slots.GetValueOrDefault(slot) is { Claimed: false } open ? open.Name : null)
            is { } refusal)
            return Decision.Reject(refusal);

        return Decision.Accept(
            added, new MemberInvited(command.MemberId, command.InviteId, command.Now + Invitations.Lifetime, command.By));
    }
}
