using ShareExpenses.Shared;

namespace ShareExpenses.Slices.AddMember;

/// <summary>Someone in the group, as the screen lists them.</summary>
/// <param name="Status">You, Joined, Invited, Invite expired or Not invited.</param>
public sealed record Person(MemberId MemberId, string Name, string Status);

/// <summary>
/// What the Add member screen shows: the people in the group, the new member's id,
/// chosen when the form is shown, and what was entered — empty at first, as
/// submitted after a rejection, with its reason. Public: the screen takes it as a
/// component parameter (spec §3).
/// </summary>
public sealed record AddMemberForm(
    GroupId GroupId,
    string GroupName,
    MemberId MemberId,
    IReadOnlyList<Person> People,
    string DisplayName,
    string Email,
    string? Error)
{
    public static AddMemberForm Blank(State state, GroupId groupId, MemberId memberId, UserId user, DateTimeOffset now)
    {
        var own = state.Members.GetValueOrDefault(user);
        var people = state.Order.Select(id =>
        {
            var slot = state.Slots[id];
            var status = slot.Claimed ? (id == own ? "You" : "Joined")
                : slot.InviteExpiresAt is not { } expires ? "Not invited"
                : expires > now ? "Invited"
                : "Invite expired";
            return new Person(id, slot.Name, status);
        });
        return new AddMemberForm(groupId, state.GroupName, memberId, [.. people], "", "", Error: null);
    }
}
