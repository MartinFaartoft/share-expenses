using ShareExpenses.Shared;

namespace ShareExpenses.Slices.InviteMember;

// Owned by this slice: it is the first to emit it (spec §12). Additive changes
// only (spec §11): a change of shape is a new type plus an upcaster.

/// <summary>
/// A member slot was invited at <paramref name="Email"/>, by user <paramref name="By"/>.
/// <paramref name="TokenHash"/> is the SHA-256 of the invite link's token — never the
/// token itself. The newest invite for a slot is the live one.
/// </summary>
public sealed record MemberInvited(MemberId MemberId, string Email, string TokenHash, UserId By);
