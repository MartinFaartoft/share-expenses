using ShareExpenses.Shared;
using ShareExpenses.Slices.CreateGroup;

namespace ShareExpenses.Slices.AddMember;

/// <summary>The command, as in <c>event-model.yaml</c>. Inputs are raw; deciding validates them.</summary>
internal sealed record Command(GroupId GroupId, string? DisplayName, UserId By);

/// <summary>Specs: <c>docs/event-model/slice-02-add-member.md</c>.</summary>
internal static class Decider
{
    /// <summary>
    /// The single answer for "no such group" and "not a member", so a non-member
    /// cannot probe which groups exist.
    /// </summary>
    public const string GroupNotFound = "group not found";

    /// <param name="state">The group's state, or null if its stream does not exist.</param>
    /// <param name="memberId">The new member slot's id, chosen by the caller.</param>
    public static Decision Decide(State? state, Command command, MemberId memberId)
    {
        // Membership first: a non-member learns nothing, not even from validation.
        if (state is null || !state.Members.Contains(command.By))
            return Decision.NotFound(GroupNotFound);

        var name = command.DisplayName?.Trim() ?? "";
        if (name.Length == 0)
            return Decision.Reject("name is required");
        if (Names.VisibleLength(name) > Names.MaxDisplayName)
            return Decision.Reject($"name must be at most {Names.MaxDisplayName} characters");
        if (state.NameKeys.Contains(Names.ComparisonKey(name)))
            return Decision.Reject("a member with that name already exists");

        return Decision.Accept(new MemberAdded(memberId, name, command.By));
    }
}
