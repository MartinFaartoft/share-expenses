using SplitIt.Shared;

namespace SplitIt.Slices.RemoveExpense;

/// <summary>
/// The expense <paramref name="ExpenseId"/> was removed by user <paramref name="By"/>, any member.
/// Not a delete (spec §7): the <c>ExpenseRecorded</c> stays, and read models fold both.
/// </summary>
public sealed record ExpenseRemoved(ExpenseId ExpenseId, UserId By);
