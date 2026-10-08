using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.RenameGroup;

namespace SplitIt.Slices.AddMember;

/// <summary>A member slot as AddMember sees it: its invite's deadline, if it was ever invited.</summary>
public sealed record Slot(string Name, bool Claimed, DateTimeOffset? InviteExpiresAt);

/// <summary>
/// What AddMember needs to know about a group, folded from its stream by Marten.
/// No stream means no state: the group does not exist. The Add member screen is built
/// from it as well as decided against. Email addresses are not in the stream; the
/// invite guards work from looked-up values on the command.
///
/// <see cref="Order"/> is member-added order.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-02-add-member.md:
///   MemberClaimReleased → un-claim the slot, drop the user from Members (invitable again)
///   MemberRemoved       → drop the slot from Order and Slots (its name is reusable)
///   MemberRenamed       → rename the slot                    (old name free, new name taken)
///   GroupArchived / GroupUnarchived → track Archived         (no changes to an archived group)
///
/// Public: Wolverine fetches it for the endpoint (spec §12). The alias is required:
/// every slice has a State.
/// </summary>
[DocumentAlias("add_member_state")]
public sealed record State(
    string GroupName,
    ImmutableDictionary<UserId, MemberId> Members,
    ImmutableList<MemberId> Order,
    ImmutableDictionary<MemberId, Slot> Slots)
{
    /// <summary>The stream id, set by Marten; Wolverine needs it to type the aggregate's identity.</summary>
    public GroupId Id { get; init; }

    public static State Create(GroupCreated e) =>
        new(e.Name, ImmutableDictionary<UserId, MemberId>.Empty, [], ImmutableDictionary<MemberId, Slot>.Empty);

    public State Apply(GroupRenamed e) => this with { GroupName = e.Name };

    public State Apply(MemberAdded e) => this with
    {
        Order = Order.Add(e.MemberId),
        Slots = Slots.SetItem(e.MemberId, new Slot(e.DisplayName, Claimed: false, InviteExpiresAt: null)),
    };

    public State Apply(MemberClaimed e) => this with
    {
        Members = Members.SetItem(e.UserId, e.MemberId),
        Slots = Slots.SetItem(e.MemberId, Slots[e.MemberId] with { Claimed = true }),
    };

    public State Apply(MemberInvited e) =>
        this with { Slots = Slots.SetItem(e.MemberId, Slots[e.MemberId] with { InviteExpiresAt = e.ExpiresAt }) };

    internal bool NameTaken(string name) =>
        Slots.Values.Any(s => Names.ComparisonKey(s.Name) == Names.ComparisonKey(name));
}
