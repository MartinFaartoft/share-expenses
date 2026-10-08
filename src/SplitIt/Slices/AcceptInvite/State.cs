using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.AddMember;
using SplitIt.Slices.ArchiveGroup;

namespace SplitIt.Slices.AcceptInvite;

/// <summary>A slot's current invite, while the slot is unclaimed; expiry aside.</summary>
/// <param name="Order">How many invites the group had sent before this one: higher is newer.</param>
public sealed record OpenInvite(InviteId InviteId, DateTimeOffset ExpiresAt, int Order);

/// <summary>
/// What AcceptInvite decides against, folded from the group stream by Marten
/// (<c>FetchForWriting</c>). No stream means no state: the group does not exist.
///
/// Close to ViewHomepage's invite state, deliberately not shared (spec §11, §12): slices fold
/// what they need and change independently.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-05-accept-invite.md:
///   MemberClaimReleased → drop the user from Members; the slot is claimable again
///                         once re-invited (the old invite stays closed)
///   MemberRemoved       → close the slot's invite
///   MemberRenamed       → rename the slot              (rejections name it)
///   GroupUnarchived     → clear Archived (deferred, spec §11)
///
/// Public, as is <see cref="OpenInvite"/>: Wolverine fetches it for the endpoint, so it
/// is in the endpoint's signature. The alias is required: every slice has a State
/// (spec §12).
/// </summary>
[DocumentAlias("accept_invite_state")]
public sealed record State(
    ImmutableDictionary<MemberId, string> SlotNames,
    ImmutableDictionary<UserId, MemberId> Members,
    ImmutableDictionary<MemberId, OpenInvite> OpenInvites,
    int InvitesSent)
{
    /// <summary>The stream id, set by Marten; Wolverine needs it to type the aggregate's identity.</summary>
    public GroupId Id { get; init; }

    /// <summary>Whether the group is archived: it takes no command (slice-15-archive-group.md).</summary>
    public bool Archived { get; init; }

    public static State Create(GroupCreated _) => new([], [], [], 0);

    public State Apply(GroupArchived e) => this with { Archived = true };

    public State Apply(MemberAdded e) => this with { SlotNames = SlotNames.SetItem(e.MemberId, e.DisplayName) };

    public State Apply(MemberClaimed e) => this with
    {
        Members = Members.SetItem(e.UserId, e.MemberId),
        OpenInvites = OpenInvites.Remove(e.MemberId),
    };

    public State Apply(MemberInvited e) => this with
    {
        OpenInvites = OpenInvites.SetItem(e.MemberId, new OpenInvite(e.InviteId, e.ExpiresAt, InvitesSent)),
        InvitesSent = InvitesSent + 1,
    };
}
