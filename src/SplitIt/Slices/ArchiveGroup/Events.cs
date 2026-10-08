using SplitIt.Shared;

namespace SplitIt.Slices.ArchiveGroup;

/// <summary>
/// The group was archived by user <paramref name="By"/>, any member. Not a delete (spec §7):
/// the group, its members and its money are read as before. What changes is what is accepted —
/// from now on no command on the group (spec §11) — and how it is shown: read-only, and out
/// of the main list. There is no way back yet (slice-15-archive-group.md).
/// </summary>
public sealed record GroupArchived(UserId By);
