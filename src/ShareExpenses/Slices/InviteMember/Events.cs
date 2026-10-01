using ShareExpenses.Shared;

namespace ShareExpenses.Slices.InviteMember;

// Owned by this slice: it is the first to emit it (spec §12). Additive changes
// only (spec §11): a change of shape is a new type plus an upcaster.

/// <summary>
/// A member slot was invited, by user <paramref name="By"/>. <paramref name="TokenHash"/>
/// is the SHA-256 of the invite link's token — never the token itself. The newest
/// invite for a slot is the live one.
///
/// Deliberately no email address: personal data stays out of the immutable ledger.
/// The address lives in the erasable <see cref="InviteDelivery"/> document (spec §3, §11).
/// </summary>
public sealed record MemberInvited(MemberId MemberId, string TokenHash, UserId By);
