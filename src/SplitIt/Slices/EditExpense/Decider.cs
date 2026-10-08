using SplitIt.Shared;

namespace SplitIt.Slices.EditExpense;

/// <summary>The command, as in <c>event-model.yaml</c>. Inputs are raw; deciding validates them.</summary>
/// <param name="AmountMinor">In the group currency's minor unit; null if what was typed is not an amount.</param>
/// <param name="Split">As entered: well-formed — a malformed split does not read — but not yet checked.</param>
/// <param name="Now">The clock, read by the caller, so deciding stays pure and testable.</param>
/// <remarks><c>splits</c>, in the model, is not here: deciding computes it (spec §6).</remarks>
internal sealed record Command(
    ExpenseId ExpenseId,
    string? Description,
    long? AmountMinor,
    MemberId? PayerMemberId,
    ExpenseSplit? Split,
    DateOnly? PaidOn,
    DateTimeOffset Now,
    UserId By);

/// <summary>Specs: <c>docs/event-model/slice-12-edit-expense.md</c>.</summary>
internal static class Decider
{
    /// <summary>The single answer for "no such group" and "not a member".</summary>
    public const string GroupNotFound = "group not found";

    /// <summary>An archived group takes no command (slice-15-archive-group.md).</summary>
    public const string GroupArchived = "group is archived";

    public const string ExpenseNotFound = "expense not found";

    public const int MaxDescription = 100;

    /// <summary>Ten billion pounds: exact as a JSON number anywhere (2⁵³), and catches extra zeros (spec §6).</summary>
    public const long MaxAmountMinor = 1_000_000_000_000;

    public static Decision Decide(State? state, Command command)
    {
        // Membership first: a non-member learns nothing, not even from validation.
        if (state is null || !state.Members.ContainsKey(command.By))
            return Decision.NotFound(GroupNotFound);

        if (state.Archived)
            return Decision.Reject(GroupArchived);

        if (!state.Expenses.TryGetValue(command.ExpenseId, out var current))
            return Decision.NotFound(ExpenseNotFound);

        var description = command.Description?.Trim() ?? "";
        if (description.Length == 0)
            return Decision.Reject("description is required");
        if (Names.VisibleLength(description) > MaxDescription)
            return Decision.Reject($"description must be at most {MaxDescription} characters");

        if (command.AmountMinor is not { } total)
            return Decision.Reject("amount must be a number");
        if (total <= 0)
            return Decision.Reject("amount must be positive");
        if (total > MaxAmountMinor)
            return Decision.Reject("amount is too large");

        if (command.PayerMemberId is not { } payer || !state.Slots.Contains(payer))
            return Decision.Reject("payer is not a member of the group");

        if (command.Split is not { } entered)
            return Decision.Reject("a split is required");
        var members = entered.Members();
        if (members.Any(m => !state.Slots.Contains(m)))
            return Decision.Reject("participant is not a member of the group");
        if (members.Count == 0)
            return Decision.Reject("at least one participant is required");
        if (members.Distinct().Count() != members.Count)
            return Decision.Reject("a participant appears more than once");

        // Recorded as entered, but in member-added order: the event's shape does not
        // depend on the order of a request, and the splits are computed in that order.
        var split = entered.OrderedBy(state.Slots.IndexOf);
        switch (split)
        {
            case SharesSplit shares when shares.Shares.Any(s => s.Shares <= 0):
                return Decision.Reject("every share must be a positive whole number");
            case ExactSplit exact when exact.Amounts.Any(a => a.AmountMinor < 0):
                return Decision.Reject("every exact amount must be zero or more");
            // 128-bit: amounts are unbounded on the way in, so their sum may not fit a long.
            case ExactSplit exact when exact.Amounts.Aggregate(Int128.Zero, (sum, a) => sum + a.AmountMinor) != total:
                return Decision.Reject("exact amounts must add up to the total");
        }

        if (command.PaidOn is not { } paidOn)
            return Decision.Reject("date is required");
        // Up to a day ahead of UTC, so a user east of Greenwich is never told their
        // today is the future. Any earlier date is fine: flights are booked first.
        if (paidOn > DateOnly.FromDateTime(command.Now.UtcDateTime).AddDays(1))
            return Decision.Reject("date cannot be in the future");

        if (current.Is(description, total, payer, split, paidOn))
            return Decision.Unchanged("nothing changed");

        IReadOnlyList<Split> splits = split switch
        {
            EqualSplit equal => Splits.SplitEqually(total, payer, equal.Participants),
            SharesSplit shares => Splits.SplitByShares(total, payer, [.. shares.Shares.Select(s => (s.MemberId, s.Shares))]),
            ExactSplit exact => [.. exact.Amounts.Select(a => new Split(a.MemberId, a.AmountMinor))],
            _ => throw new InvalidOperationException($"Unknown split {split.GetType().Name}"),
        };

        return Decision.Accept(new ExpenseEdited(
            command.ExpenseId, description, total, payer, split, splits, paidOn, command.By));
    }
}
