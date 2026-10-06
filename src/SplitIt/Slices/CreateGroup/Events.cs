using SplitIt.Shared;

namespace SplitIt.Slices.CreateGroup;

public sealed record GroupCreated(GroupId GroupId, string Name, string Currency, UserId CreatedBy);

public sealed record MemberAdded(MemberId MemberId, string DisplayName, UserId By);

public sealed record MemberClaimed(MemberId MemberId, UserId UserId);
