using System.Collections.Immutable;
using Marten.Schema;
using ShareExpenses.Shared;
using ShareExpenses.Slices.CreateGroup;

namespace ShareExpenses.Slices.InviteMember;

/// <summary>A member slot as InviteMember sees it.</summary>
internal sealed record Slot(string Name, bool Claimed);

/// <summary>
/// What InviteMember needs to know about a group, folded from its stream by Marten
/// (<c>FetchForWriting</c>). No stream means no state: the group does not exist.
/// Email addresses are not in the stream; the guards that need them work from
/// looked-up values on the command (see <see cref="Command"/>).
///
/// FOLD CHECKLIST — this state is private to the slice (spec §12), so nothing
/// forces it to keep up with new events. When these slices are built, fold their
/// events here and add the deferred specs in slice-03-invite-member.md:
///   MemberClaimReleased → un-claim the slot, drop the user from Members (invitable again)
///   MemberRemoved       → drop the slot                    (cannot be invited)
///   MemberRenamed       → rename the slot                  (rejections name it)
///   GroupArchived / GroupUnarchived → track Archived       (no changes to an archived group)
///
/// Internal, as is <see cref="Slot"/>, until the slice has a screen: Wolverine will
/// fetch it for the endpoint, putting it in the endpoint's signature, so public (spec
/// §12). The alias is required: every slice has a State, and Marten would
/// otherwise name them all ledger.state.
/// </summary>
[DocumentAlias("invite_member_state")]
internal sealed record State(
    string GroupName,
    ImmutableDictionary<UserId, MemberId> Members,
    ImmutableDictionary<MemberId, Slot> Slots)
{
    /// <summary>The stream id, set by Marten; Wolverine needs it to type the aggregate's identity.</summary>
    public GroupId Id { get; init; }

    public static State Create(GroupCreated e) => new(e.Name, [], []);

    public State Apply(MemberAdded e) =>
        this with { Slots = Slots.SetItem(e.MemberId, new Slot(e.DisplayName, Claimed: false)) };

    public State Apply(MemberClaimed e) => this with
    {
        Members = Members.SetItem(e.UserId, e.MemberId),
        Slots = Slots.SetItem(e.MemberId, Slots[e.MemberId] with { Claimed = true }),
    };
}
