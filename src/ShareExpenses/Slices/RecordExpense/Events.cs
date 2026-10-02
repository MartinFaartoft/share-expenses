using ShareExpenses.Shared;

namespace ShareExpenses.Slices.RecordExpense;

// Owned by this slice: it is the first to emit it (spec §12). Additive changes
// only (spec §11): a change of shape is a new type plus an upcaster.

/// <summary>
/// An expense was recorded, by user <paramref name="By"/>: <paramref name="PayerMemberId"/>
/// paid <paramref name="AmountMinor"/>, in the group currency's minor unit, on
/// <paramref name="PaidOn"/>.
///
/// Carries both the split as entered — <paramref name="Split"/>, one shape per mode
/// (spec §7) — and the <paramref name="Splits"/> it produced, which sum to the amount
/// (spec §6). The splits are the facts the group agreed to: never re-derived from the
/// split. Both are in member-added order.
///
/// <paramref name="PaidOn"/> is when the money was spent, a domain fact; when it was
/// recorded is Marten metadata (spec §11).
/// </summary>
public sealed record ExpenseRecorded(
    ExpenseId ExpenseId,
    string Description,
    long AmountMinor,
    MemberId PayerMemberId,
    ExpenseSplit Split,
    IReadOnlyList<Shared.Split> Splits,
    DateOnly PaidOn,
    UserId By);
