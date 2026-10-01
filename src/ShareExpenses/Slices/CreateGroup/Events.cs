namespace ShareExpenses.Slices.CreateGroup;

// Owned by this slice: it is the first to emit each of them (spec §12). Other
// slices emit and fold these same records; they never redeclare them.
//
// Additive changes only (spec §11): never rename or reshape these. A change of
// shape is a new type plus an upcaster.

/// <summary>A group came into existence. The stream id is <paramref name="GroupId"/>.</summary>
public sealed record GroupCreated(Guid GroupId, string Name, string Currency, Guid CreatedBy);

/// <summary>A member slot was added to the group, by name alone.</summary>
public sealed record MemberAdded(Guid MemberId, string DisplayName);

/// <summary>A user took a member slot as theirs.</summary>
public sealed record MemberClaimed(Guid MemberId, Guid UserId);
