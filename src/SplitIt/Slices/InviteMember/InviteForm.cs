using SplitIt.Shared;

namespace SplitIt.Slices.InviteMember;

/// <summary>
/// What the Invite member screen shows: who is being invited, and what was entered —
/// empty at first, as submitted after a rejection, with its reason. Public: the
/// screen takes it as a component parameter (spec §3).
/// </summary>
public sealed record InviteForm(
    GroupId GroupId,
    MemberId MemberId,
    string MemberName,
    string Email,
    string? Error)
{
    public static InviteForm Blank(State state, GroupId groupId, MemberId memberId) =>
        new(groupId, memberId, state.Slots[memberId].Name, "", Error: null);

    public InviteForm Rejected(string email, string error) => this with { Email = email, Error = error };
}
