using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.RenameGroup;
using SplitIt.Slices.ArchiveGroup;

namespace SplitIt.Slices.InviteMember;

/// <summary>A member slot as InviteMember sees it.</summary>
public sealed record Slot(string Name, bool Claimed);

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
///   GroupUnarchived     → clear Archived (deferred, spec §11)
///
/// Public, as is <see cref="Slot"/>: Wolverine fetches it for the endpoint, putting it
/// in the endpoint's signature (spec §12). The alias is required: every slice has a State, and Marten would
/// otherwise name them all ledger.state.
/// </summary>
[DocumentAlias("invite_member_state")]
public sealed record State(
    string GroupName,
    ImmutableDictionary<UserId, MemberId> Members,
    ImmutableDictionary<MemberId, Slot> Slots)
{
    /// <summary>The stream id, set by Marten; Wolverine needs it to type the aggregate's identity.</summary>
    public GroupId Id { get; init; }

    /// <summary>Whether the group is archived: it takes no command (slice-15-archive-group.md).</summary>
    public bool Archived { get; init; }

    public static State Create(GroupCreated e) => new(e.Name, [], []);

    public State Apply(GroupArchived e) => this with { Archived = true };

    public State Apply(GroupRenamed e) => this with { GroupName = e.Name };

    public State Apply(MemberAdded e) =>
        this with { Slots = Slots.SetItem(e.MemberId, new Slot(e.DisplayName, Claimed: false)) };

    public State Apply(MemberClaimed e) => this with
    {
        Members = Members.SetItem(e.UserId, e.MemberId),
        Slots = Slots.SetItem(e.MemberId, Slots[e.MemberId] with { Claimed = true }),
    };
}
