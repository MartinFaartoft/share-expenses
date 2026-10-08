using System.Globalization;
using SplitIt.Shared;

namespace SplitIt.Slices.ChangeDefaultSplit;

/// <summary>A member slot as the form offers it.</summary>
public sealed record FormMember(MemberId MemberId, string Name, bool IsYou);

/// <summary>
/// What the Default split form shows: the default in force at first, as submitted after a
/// rejection, with its reason. Public: the screen takes it as a component parameter (spec §3).
/// </summary>
/// <param name="Mode"><c>equal</c> or <c>shares</c>; which row field the form shows.</param>
/// <param name="Participants">Who shares by default; a member not checked is left out.</param>
/// <param name="Shares">Each slot's shares field, as typed — for every slot, so equal keeps what shares had.</param>
public sealed record DefaultSplitForm(
    GroupId GroupId,
    IReadOnlyList<FormMember> Members,
    string Mode,
    IReadOnlySet<MemberId> Participants,
    IReadOnlyDictionary<MemberId, string> Shares,
    string? Error)
{
    public static bool IsMode(string mode) => mode is DefaultSplit.Equal or DefaultSplit.SharesMode;

    /// <summary>The form for the default in force: its mode, who is in, and the shares of those who are.</summary>
    public static DefaultSplitForm Of(State state, GroupId groupId, UserId user)
    {
        var members = state.Slots
            .Select(slot => new FormMember(slot, state.Names[slot], state.Members.TryGetValue(user, out var own) && own == slot))
            .ToList();
        return new DefaultSplitForm(groupId, members, state.Default.Mode,
            Participants: members.Where(m => state.Default.ShareOf(m.MemberId) > 0).Select(m => m.MemberId).ToHashSet(),
            Shares: members.ToDictionary(m => m.MemberId, m =>
                (state.Default.ShareOf(m.MemberId) is > 0 and var share ? share : 1).ToString(CultureInfo.InvariantCulture)),
            Error: null);
    }

    public DefaultSplitForm Rejected(string error) => this with { Error = error };
}
