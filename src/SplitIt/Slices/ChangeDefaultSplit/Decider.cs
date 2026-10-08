using SplitIt.Shared;

namespace SplitIt.Slices.ChangeDefaultSplit;

/// <summary>The command, as in <c>event-model.yaml</c>. Inputs are raw; deciding validates them.</summary>
/// <param name="Mode">As entered: anything, until deciding checks it.</param>
/// <param name="Shares">As entered: a share per member listed, 0 is left out, a member not listed counts as 1.</param>
internal sealed record Command(string? Mode, IReadOnlyList<MemberShares> Shares, UserId By);

/// <summary>Specs: <c>docs/event-model/slice-16-change-default-split.md</c>.</summary>
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

        if (command.Mode is not (DefaultSplit.Equal or DefaultSplit.SharesMode))
            return Decision.Reject("default split must be equal or shares");
        var mode = command.Mode;

        if (command.Shares.Any(s => !state.Slots.Contains(s.MemberId)))
            return Decision.Reject("participant is not a member of the group");
        if (command.Shares.Select(s => s.MemberId).Distinct().Count() != command.Shares.Count)
            return Decision.Reject("a participant appears more than once");
        if (command.Shares.Any(s => s.Shares < 0))
            return Decision.Reject("every share must be zero or more");

        // Normalised, in member-added order: a member not listed is 1, and only what is not 1
        // is recorded; for equal, any share above zero is 1.
        var normalised = state.Slots
            .Select(slot => (slot, share: command.Shares.FirstOrDefault(s => s.MemberId == slot)?.Shares ?? 1))
            .Select(x => (x.slot, share: mode == DefaultSplit.Equal ? Math.Min(x.share, 1) : x.share))
            .ToList();

        if (normalised.All(x => x.share == 0))
            return Decision.Reject("at least one member must share by default");

        var proposed = new DefaultSplit(mode,
            [.. normalised.Where(x => x.share != 1).Select(x => new MemberShares(x.slot, x.share))]);
        if (proposed.Equals(state.Default))
            return Decision.Unchanged("nothing changed");

        return Decision.Accept(new GroupDefaultSplitChanged(proposed.Mode, proposed.Shares, command.By));
    }
}
