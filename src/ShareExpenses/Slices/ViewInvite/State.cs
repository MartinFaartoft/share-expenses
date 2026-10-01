using System.Collections.Immutable;
using Marten.Schema;
using ShareExpenses.Shared;
using ShareExpenses.Slices.CreateGroup;
using ShareExpenses.Slices.InviteMember;

namespace ShareExpenses.Slices.ViewInvite;

/// <summary>An invite that can still be used, apart from its deadline.</summary>
/// <param name="InvitedBy">The slot the inviting user held when inviting.</param>
internal sealed record OpenInvite(string TokenHash, DateTimeOffset ExpiresAt, MemberId InvitedBy);

/// <summary>
/// What this slice folds from the group stream (spec §12 naming: every slice folds a
/// <c>State</c>). Not the read model: it holds every open invite, hash and deadline,
/// and <see cref="Reader"/> selects the one read model the query asks for —
/// <see cref="InviteReadModel"/>, the shape the screen receives. Folded <em>live</em>
/// from one group stream per request (spec §11): nothing is stored. No stream, no state.
///
/// Only the newest invite per unclaimed slot is open; re-inviting replaces it and
/// claiming closes it. Expiry is not folded — it depends on the clock, so the query
/// checks it against the recorded deadline.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-04-view-invite.md:
///   MemberRemoved       → close the slot's invite
///   MemberRenamed       → rename the slot           (names shown as they are now)
///   GroupRenamed        → rename the group
///   GroupArchived       → decide whether invites stay viewable
///   MemberClaimReleased → (old invites stay dead; a new invite reopens the slot)
///
/// Convention methods must be public for Marten's source generator; the type is
/// internal. The alias is required: every slice has a State (spec §12).
/// </summary>
[DocumentAlias("view_invite_state")]
internal sealed record State(
    string GroupName,
    ImmutableDictionary<MemberId, string> SlotNames,
    ImmutableDictionary<UserId, MemberId> Members,
    ImmutableDictionary<MemberId, OpenInvite> OpenInvites)
{
    public static State Create(GroupCreated e) => new(e.Name, [], [], []);

    public State Apply(MemberAdded e) => this with { SlotNames = SlotNames.SetItem(e.MemberId, e.DisplayName) };

    public State Apply(MemberClaimed e) => this with
    {
        Members = Members.SetItem(e.UserId, e.MemberId),
        OpenInvites = OpenInvites.Remove(e.MemberId),
    };

    public State Apply(MemberInvited e) =>
        this with { OpenInvites = OpenInvites.SetItem(e.MemberId, new OpenInvite(e.TokenHash, e.ExpiresAt, Members[e.By])) };
}
