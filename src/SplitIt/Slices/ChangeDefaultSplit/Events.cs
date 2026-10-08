using SplitIt.Shared;

namespace SplitIt.Slices.ChangeDefaultSplit;

/// <summary>
/// The group's default split was changed by user <paramref name="By"/>, any member: how the
/// Add expense form opens (slice-16-change-default-split.md).
///
/// <paramref name="Mode"/> is <c>equal</c> or <c>shares</c>. <paramref name="Shares"/> lists the
/// members whose share is <b>not 1</b>, in member-added order; <c>0</c> is left out, and a member
/// not listed counts as <c>1</c> — so a member added later starts in. For <c>equal</c>, only the
/// <c>0</c>s are recorded. Normalised, so "the same default" is a plain comparison.
/// </summary>
public sealed record GroupDefaultSplitChanged(string Mode, IReadOnlyList<MemberShares> Shares, UserId By);
