using SplitIt.Shared;

namespace SplitIt.Slices.ViewExpense;

/// <summary>The query, as in <c>event-model.yaml</c>.</summary>
/// <param name="UserId">The signed-in user: members only, and who "you" is.</param>
internal sealed record Query(ExpenseId ExpenseId, UserId UserId);

/// <summary>
/// The read model, as in <c>event-model.yaml</c>: exactly what the expense sheet receives.
/// Public, with its parts, because the screen takes it as a component parameter (spec §3).
/// </summary>
/// <param name="You">The caller's own slot.</param>
/// <param name="Currency">ISO 4217; amounts are in its minor unit.</param>
/// <param name="Mode">How it is split, as stored: <c>equal</c>, <c>shares</c> or <c>exact</c>.</param>
/// <param name="Archived">The group is archived: no Edit, no Remove (slice-15-archive-group.md).</param>
/// <param name="Shares">Who shares it, in member-added order, with what each owes.</param>
public sealed record ExpenseReadModel(
    GroupId GroupId,
    ExpenseId ExpenseId,
    string Currency,
    MemberId You,
    string Description,
    long AmountMinor,
    MemberId PayerMemberId,
    string PayerName,
    DateOnly PaidOn,
    string Mode,
    IReadOnlyList<ExpenseShare> Shares,
    bool Archived);

/// <param name="AmountMinor">What this member owes: the recorded amount, never re-derived (spec §6).</param>
/// <param name="Weight">Their share, for a shares split; otherwise null.</param>
public sealed record ExpenseShare(MemberId MemberId, string Name, long AmountMinor, int? Weight);

/// <summary>Specs: <c>docs/event-model/slice-13-view-expense.md</c>.</summary>
internal static class Reader
{
    /// <param name="state">The folded group, or null if it does not exist.</param>
    /// <returns>Null — "not found" — for a missing group, a non-member, and a missing or removed expense alike.</returns>
    public static ExpenseReadModel? Read(State? state, Query query)
    {
        if (state?.Slots.FirstOrDefault(s => s.ClaimedBy == query.UserId) is not { } you
            || !state.Expenses.TryGetValue(query.ExpenseId, out var expense))
            return null;

        var weights = (expense.Split as SharesSplit)?.Shares.ToDictionary(s => s.MemberId, s => s.Shares);
        var shares = state.Slots
            .Select(slot => (slot, split: expense.Splits.FirstOrDefault(s => s.MemberId == slot.MemberId)))
            .Where(x => x.split is not null)
            .Select(x => new ExpenseShare(
                x.slot.MemberId, x.slot.Name, x.split!.AmountMinor,
                weights is not null && weights.TryGetValue(x.slot.MemberId, out var weight) ? weight : null))
            .ToList();

        return new ExpenseReadModel(
            GroupId.From(state.Id), query.ExpenseId, state.Currency, you.MemberId, expense.Description, expense.AmountMinor,
            expense.PayerMemberId, state.Slots.Single(s => s.MemberId == expense.PayerMemberId).Name, expense.PaidOn,
            expense.Split switch
            {
                EqualSplit => "equal",
                SharesSplit => "shares",
                ExactSplit => "exact",
                _ => throw new InvalidOperationException($"Unknown split {expense.Split.GetType().Name}"),
            },
            shares,
            state.Archived);
    }
}
