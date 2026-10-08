using SplitIt.Shared;

namespace SplitIt.Slices.RenameGroup;

/// <summary>The command, as in <c>event-model.yaml</c>. The input is raw; deciding validates it.</summary>
internal sealed record Command(string? Name, UserId By);

/// <summary>Specs: <c>docs/event-model/slice-14-rename-group.md</c>.</summary>
internal static class Decider
{
    /// <summary>The single answer for "no such group" and "not a member".</summary>
    public const string GroupNotFound = "group not found";

    /// <summary>An archived group takes no command (slice-15-archive-group.md).</summary>
    public const string GroupArchived = "group is archived";

    public static Decision Decide(State? state, Command command)
    {
        // Membership first: a non-member learns nothing, not even from validation.
        if (state is null || !state.Members.ContainsKey(command.By))
            return Decision.NotFound(GroupNotFound);

        if (state.Archived)
            return Decision.Reject(GroupArchived);

        var name = command.Name?.Trim() ?? "";
        if (name.Length == 0)
            return Decision.Reject("name is required");
        if (Names.VisibleLength(name) > Names.GroupNameMaxLength)
            return Decision.Reject($"name must be at most {Names.GroupNameMaxLength} characters");

        if (name == state.GroupName)
            return Decision.Unchanged("nothing changed");

        return Decision.Accept(new GroupRenamed(name, command.By));
    }
}
