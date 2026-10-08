using SplitIt.Shared;

namespace SplitIt.Slices.EditExpense;

/// <summary>
/// The expense <paramref name="ExpenseId"/> was edited by user <paramref name="By"/>, any
/// member: these are its values now, all of them (spec §11). Not a delete (spec §7): the
/// <c>ExpenseRecorded</c> stays, and read models fold both.
///
/// Carries the split as entered and the <paramref name="Splits"/> it produced, in
/// member-added order, as <c>ExpenseRecorded</c> does (spec §6): recomputed on every
/// edit, recorded whether or not they moved. Never the old values: a fold holds them.
/// </summary>
public sealed record ExpenseEdited(
    ExpenseId ExpenseId,
    string Description,
    long AmountMinor,
    MemberId PayerMemberId,
    ExpenseSplit Split,
    IReadOnlyList<Split> Splits,
    DateOnly PaidOn,
    UserId By);
