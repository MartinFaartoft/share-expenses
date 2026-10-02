using System.Collections.Immutable;
using Marten.Schema;
using ShareExpenses.Shared;
using ShareExpenses.Slices.CreateGroup;
using ShareExpenses.Slices.InviteMember;

namespace ShareExpenses.Slices.AcceptInvite;

/// <summary>An invite that can still be claimed, apart from its deadline.</summary>
public sealed record OpenInvite(string TokenHash, DateTimeOffset ExpiresAt);

/// <summary>
/// What AcceptInvite decides against, folded from the group stream by Marten
/// (<c>FetchForWriting</c>). No stream means no state: the group does not exist.
///
/// Close to ViewInvite's state, deliberately not shared (spec §11, §12): slices fold
/// what they need and change independently.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-05-claim-member.md:
///   MemberClaimReleased → drop the user from Members; the slot is claimable again
///                         once re-invited (the old invite stays closed)
///   MemberRemoved       → close the slot's invite
///   MemberRenamed       → rename the slot              (rejections name it)
///   GroupArchived / GroupUnarchived → track Archived   (no claiming into an archived group)
///
/// Public, as is <see cref="OpenInvite"/>: Wolverine fetches it for the endpoint, so
/// it is in the endpoint's signature. The alias is required: every slice has a State
/// (spec §12).
/// </summary>
[DocumentAlias("accept_invite_state")]
public sealed record State(
    ImmutableDictionary<MemberId, string> SlotNames,
    ImmutableDictionary<UserId, MemberId> Members,
    ImmutableDictionary<MemberId, OpenInvite> OpenInvites)
{
    /// <summary>The stream id, set by Marten; Wolverine needs it to type the aggregate's identity.</summary>
    public GroupId Id { get; init; }

    public static State Create(GroupCreated _) => new([], [], []);

    public State Apply(MemberAdded e) => this with { SlotNames = SlotNames.SetItem(e.MemberId, e.DisplayName) };

    public State Apply(MemberClaimed e) => this with
    {
        Members = Members.SetItem(e.UserId, e.MemberId),
        OpenInvites = OpenInvites.Remove(e.MemberId),
    };

    public State Apply(MemberInvited e) =>
        this with { OpenInvites = OpenInvites.SetItem(e.MemberId, new OpenInvite(e.TokenHash, e.ExpiresAt)) };
}
