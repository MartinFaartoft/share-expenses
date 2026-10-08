using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.EditExpense;
using SplitIt.Slices.RecordExpense;
using SplitIt.Slices.RenameGroup;

namespace SplitIt.Slices.RemoveExpense;

/// <summary>An expense as the confirm page shows it.</summary>
public sealed record RecordedExpense(string Description, long AmountMinor, MemberId PayerMemberId, DateOnly PaidOn);

/// <summary>
/// What RemoveExpense needs to know about a group: who may remove, which expenses
/// exist, and which are already removed.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-11-remove-expense.md:
///   MemberClaimReleased → drop from Members
///   MemberRenamed       → rename in Names
///   GroupArchived / GroupUnarchived → track Archived
/// </summary>
[DocumentAlias("remove_expense_state")]
public sealed record State(
    string GroupName,
    string Currency,
    ImmutableDictionary<MemberId, string> Names,
    ImmutableDictionary<UserId, MemberId> Members,
    ImmutableDictionary<ExpenseId, RecordedExpense> Expenses,
    ImmutableHashSet<ExpenseId> Removed)
{
    public GroupId Id { get; init; }

    public static State Create(GroupCreated e) =>
        new(e.Name, e.Currency, ImmutableDictionary<MemberId, string>.Empty,
            ImmutableDictionary<UserId, MemberId>.Empty, ImmutableDictionary<ExpenseId, RecordedExpense>.Empty, []);

    public State Apply(GroupRenamed e) => this with { GroupName = e.Name };

    public State Apply(MemberAdded e) => this with { Names = Names.SetItem(e.MemberId, e.DisplayName) };

    public State Apply(MemberClaimed e) => this with { Members = Members.SetItem(e.UserId, e.MemberId) };

    public State Apply(ExpenseRecorded e) =>
        this with { Expenses = Expenses.SetItem(e.ExpenseId, new RecordedExpense(e.Description, e.AmountMinor, e.PayerMemberId, e.PaidOn)) };

    public State Apply(ExpenseEdited e) =>
        this with { Expenses = Expenses.SetItem(e.ExpenseId, new RecordedExpense(e.Description, e.AmountMinor, e.PayerMemberId, e.PaidOn)) };

    public State Apply(ExpenseRemoved e) => this with { Removed = Removed.Add(e.ExpenseId) };
}
