using SplitIt.Shared;

namespace SplitIt.Slices.ArchiveGroup;

/// <summary>The command, as in <c>event-model.yaml</c>: nothing but who asks.</summary>
internal sealed record Command(UserId By);

/// <summary>Specs: <c>docs/event-model/slice-15-archive-group.md</c>.</summary>
internal static class Decider
{
    /// <summary>The single answer for "no such group" and "not a member".</summary>
    public const string GroupNotFound = "group not found";

    /// <summary>
    /// A group that is not settled up can be archived: the page warns, deciding does not
    /// look (spec §8). Nothing else can be wrong with this command.
    /// </summary>
    public static Decision Decide(State? state, Command command)
    {
        // Membership first: a non-member learns nothing.
        if (state is null || !state.Members.ContainsKey(command.By))
            return Decision.NotFound(GroupNotFound);

        if (state.Archived)
            return Decision.Unchanged("group already archived");

        return Decision.Accept(new GroupArchived(command.By));
    }
}
