using ShareExpenses.Infrastructure.Invites;
using ShareExpenses.Shared;

namespace ShareExpenses.Slices.InviteMember;

// Owned by this slice: it is the first to emit it (spec §12). Additive changes
// only (spec §11): a change of shape is a new type plus an upcaster.

/// <summary>
/// A member slot was invited, by user <paramref name="By"/>. <paramref name="InviteId"/>
/// names the <see cref="Invite"/> document that binds the slot to the address it was
/// invited at. The newest invite for a slot is its current one, until
/// <paramref name="ExpiresAt"/>.
///
/// <paramref name="ExpiresAt"/> is a deadline decided when inviting, recorded as a
/// domain fact (spec §11): changing the invite lifetime later does not move the
/// deadline of invites already sent.
///
/// Deliberately no email address: personal data stays out of the immutable ledger.
/// The address lives on the erasable <see cref="Invite"/> document (spec §3, §11).
/// </summary>
public sealed record MemberInvited(MemberId MemberId, InviteId InviteId, DateTimeOffset ExpiresAt, UserId By);
