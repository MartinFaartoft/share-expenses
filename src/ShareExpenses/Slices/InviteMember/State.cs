using System.Collections.Immutable;
using Marten.Schema;
using ShareExpenses.Shared;
using ShareExpenses.Slices.CreateGroup;

namespace ShareExpenses.Slices.InviteMember;

/// <summary>A member slot as InviteMember sees it.</summary>
/// <param name="Email">
/// The email last invited to this slot. Kept after the slot is claimed: an email
/// that has joined still holds its slot (scenario 11).
/// </param>
internal sealed record Slot(string Name, bool Claimed, string? Email);

/// <summary>
/// What InviteMember needs to know about a group, folded from its stream by Marten
/// (<c>FetchForWriting</c>). No stream means no state: the group does not exist.
///
/// FOLD CHECKLIST — this state is private to the slice (spec §12), so nothing
/// forces it to keep up with new events. When these slices are built, fold their
/// events here and add the deferred specs in slice-03-invite-member.md:
///   MemberClaimReleased → un-claim the slot, drop the user from Members (invitable again)
///   MemberRemoved       → drop the slot                    (cannot be invited)
///   MemberRenamed       → rename the slot                  (rejections name it)
///   GroupArchived / GroupUnarchived → track Archived       (no changes to an archived group)
///
/// Convention methods must be public for Marten's source generator; the type is
/// internal, so they are not visible outside the assembly. The alias is required:
/// every slice has a State, and Marten would otherwise name them all ledger.state.
/// </summary>
[DocumentAlias("invite_member_state")]
internal sealed record State(
    string GroupName,
    ImmutableDictionary<UserId, MemberId> Members,
    ImmutableDictionary<MemberId, Slot> Slots)
{
    public static State Create(GroupCreated e) => new(e.Name, [], []);

    public State Apply(MemberAdded e) =>
        this with { Slots = Slots.SetItem(e.MemberId, new Slot(e.DisplayName, Claimed: false, Email: null)) };

    public State Apply(MemberClaimed e) => this with
    {
        Members = Members.SetItem(e.UserId, e.MemberId),
        Slots = Slots.SetItem(e.MemberId, Slots[e.MemberId] with { Claimed = true }),
    };

    public State Apply(MemberInvited e) =>
        this with { Slots = Slots.SetItem(e.MemberId, Slots[e.MemberId] with { Email = e.Email }) };
}
