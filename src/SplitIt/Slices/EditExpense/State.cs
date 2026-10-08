using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.RecordExpense;
using SplitIt.Slices.RemoveExpense;

namespace SplitIt.Slices.EditExpense;

/// <summary>An expense as it stands: as recorded, with every edit since folded in.</summary>
public sealed record CurrentExpense(
    string Description, long AmountMinor, MemberId PayerMemberId, ExpenseSplit Split, DateOnly PaidOn);

/// <summary>
/// What EditExpense needs to know about a group: who may edit, the slots a payer or
/// participant may be, and each expense that can still be edited, as it stands. The
/// Edit expense screen is built from it as well as decided against.
///
/// <see cref="Slots"/> keeps member-added order, which orders participants and splits
/// and decides who gets leftover minor units (spec §6). A removed expense is dropped.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-12-edit-expense.md:
///   MemberClaimReleased → remove from Members        (a released claim ends membership)
///   MemberRemoved       → drop the slot              (cannot become payer or participant)
///   MemberRenamed       → rename in Names
///   GroupRenamed        → rename GroupName
///   GroupArchived / GroupUnarchived → track Archived (no changes to an archived group)
///
/// Public: Wolverine fetches it for the endpoint (spec §12). The alias is required:
/// every slice has a State.
/// </summary>
[DocumentAlias("edit_expense_state")]
public sealed record State(
    string GroupName,
    string Currency,
    ImmutableList<MemberId> Slots,
    ImmutableDictionary<MemberId, string> Names,
    ImmutableDictionary<UserId, MemberId> Members,
    ImmutableDictionary<ExpenseId, CurrentExpense> Expenses)
{
    /// <summary>The stream id, set by Marten; Wolverine needs it to type the aggregate's identity.</summary>
    public GroupId Id { get; init; }

    public static State Create(GroupCreated e) =>
        new(e.Name, e.Currency, [], ImmutableDictionary<MemberId, string>.Empty,
            ImmutableDictionary<UserId, MemberId>.Empty, ImmutableDictionary<ExpenseId, CurrentExpense>.Empty);

    public State Apply(MemberAdded e) =>
        this with { Slots = Slots.Add(e.MemberId), Names = Names.SetItem(e.MemberId, e.DisplayName) };

    public State Apply(MemberClaimed e) => this with { Members = Members.SetItem(e.UserId, e.MemberId) };

    public State Apply(ExpenseRecorded e) =>
        this with { Expenses = Expenses.SetItem(e.ExpenseId, new CurrentExpense(e.Description, e.AmountMinor, e.PayerMemberId, e.Split, e.PaidOn)) };

    public State Apply(ExpenseEdited e) =>
        this with { Expenses = Expenses.SetItem(e.ExpenseId, new CurrentExpense(e.Description, e.AmountMinor, e.PayerMemberId, e.Split, e.PaidOn)) };

    public State Apply(ExpenseRemoved e) => this with { Expenses = Expenses.Remove(e.ExpenseId) };
}
