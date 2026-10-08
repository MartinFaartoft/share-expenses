using SplitIt.Shared;

namespace SplitIt.Slices.RenameGroup;

/// <summary>
/// The group was renamed to <paramref name="Name"/> by user <paramref name="By"/>, any
/// member. The name before stays in <c>GroupCreated</c> and in any earlier rename: only what
/// folds the log shows the latest.
/// </summary>
public sealed record GroupRenamed(string Name, UserId By);
