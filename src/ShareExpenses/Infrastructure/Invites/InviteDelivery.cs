using Marten.Schema;
using ShareExpenses.Shared;

namespace ShareExpenses.Infrastructure.Invites;

/// <summary>
/// Where an invite was sent: a plain document, not event-sourced (spec §3), so the
/// address can be corrected or erased. One per member slot (id = member id).
///
/// Lives in Infrastructure, not in a slice, because two slices touch it: InviteMember
/// writes it (re-inviting overwrites it, in the same session as <c>MemberInvited</c>)
/// and AcceptInvite deletes it (in the same session as <c>MemberClaimed</c>) — once a
/// slot is claimed, the address is not needed, so it is not kept. Plain documents are
/// supporting state, like Identity's users, not slice logic.
///
/// Used only as a guard — "this address already has an open invite in the group" —
/// so if it ever drifts from the stream, the claim-time invariant (one user, one slot)
/// still protects the ledger. A future transactional outbox would hang delivery
/// status off this document (spec §14).
/// </summary>
[DocumentAlias("invite_delivery")]
internal sealed class InviteDelivery
{
    /// <summary>The invited member slot's id.</summary>
    public Guid Id { get; set; }

    public GroupId GroupId { get; set; }

    /// <summary>
    /// The <c>MemberInvited.TokenHash</c> this delivery belongs to: the key from a
    /// delivery to its event. Today one delivery per slot is overwritten on re-invite,
    /// so the slot alone suffices; a future outbox needs it to skip sending a delivery
    /// whose invite has since been superseded.
    /// </summary>
    public string TokenHash { get; set; } = "";

    public string Email { get; set; } = "";
}
