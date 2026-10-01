using ShareExpenses.Shared;

namespace ShareExpenses.Slices.CreateGroup;

/// <summary>The command, as in <c>event-model.yaml</c>. Inputs are raw; deciding validates them.</summary>
internal sealed record Command(string? Name, string? Currency, string? DisplayName, UserId CreatedBy);

/// <summary>
/// Specs: <c>docs/event-model/slice-01-create-group.md</c>. There is no state to
/// decide against: the stream must not exist yet, and that is enforced by Marten
/// when the stream is started, not here (scenario 4).
/// </summary>
internal static class Decider
{
    /// <param name="groupId">The new stream's id, chosen by the caller.</param>
    /// <param name="memberId">The creator's member slot, chosen by the caller.</param>
    public static Decision Decide(Command command, GroupId groupId, MemberId memberId)
    {
        var name = command.Name?.Trim() ?? "";
        if (name.Length == 0)
            return Decision.Reject("name is required");
        if (Names.VisibleLength(name) > Names.MaxGroupName)
            return Decision.Reject($"name must be at most {Names.MaxGroupName} characters");

        if (!Currency.TryNormalise(command.Currency, out var currency))
            return Decision.Reject("currency must be a known ISO 4217 code");

        var displayName = command.DisplayName?.Trim() ?? "";
        if (displayName.Length == 0)
            return Decision.Reject("your name is required");
        if (Names.VisibleLength(displayName) > Names.MaxDisplayName)
            return Decision.Reject($"your name must be at most {Names.MaxDisplayName} characters");

        // The creator is seated and claimed in the same breath: a group whose
        // creator is not a member is a meaningless state (spec §11).
        return Decision.Accept(
            new GroupCreated(groupId, name, currency, command.CreatedBy),
            new MemberAdded(memberId, displayName, command.CreatedBy),
            new MemberClaimed(memberId, command.CreatedBy));
    }
}
