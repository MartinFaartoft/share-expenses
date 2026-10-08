using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.EditExpense;
using SplitIt.Slices.RecordExpense;
using SplitIt.Slices.RemoveExpense;
using SplitIt.Slices.ArchiveGroup;

namespace SplitIt.Slices.ViewExpense;

/// <summary>A member slot as the fold keeps it.</summary>
/// <param name="ClaimedBy">The user holding the slot, if any.</param>
internal sealed record Slot(MemberId MemberId, string Name, UserId? ClaimedBy);

/// <summary>An expense as it stands: as recorded, with every edit since folded in.</summary>
internal sealed record Expense(
    string Description,
    long AmountMinor,
    MemberId PayerMemberId,
    ExpenseSplit Split,
    IReadOnlyList<Split> Splits,
    DateOnly PaidOn);

/// <summary>
/// What the expense sheet is read from, folded <em>live</em> from the group stream per
/// request — never stored (spec §11). A removed expense is dropped.
///
/// <see cref="Slots"/> keep member-added order, in which the amounts are listed.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-13-view-expense.md:
///   MemberRenamed                  → rename
///   MemberClaimReleased            → clear ClaimedBy
///   GroupUnarchived                → clear Archived (deferred, spec §11)
///
/// The alias is required: every slice has a State (spec §12).
/// </summary>
[DocumentAlias("view_expense_state")]
internal sealed record State(
    string Currency,
    ImmutableList<Slot> Slots,
    ImmutableDictionary<ExpenseId, Expense> Expenses)
{
    /// <summary>The group's stream id.</summary>
    public Guid Id { get; init; }

    /// <summary>Archived: the sheet shows the expense without its actions (slice-15-archive-group.md).</summary>
    public bool Archived { get; init; }

    public static State Create(GroupCreated e) => new(e.Currency, [], ImmutableDictionary<ExpenseId, Expense>.Empty);

    public State Apply(GroupArchived e) => this with { Archived = true };

    public State Apply(MemberAdded e) => this with { Slots = Slots.Add(new Slot(e.MemberId, e.DisplayName, null)) };

    public State Apply(MemberClaimed e) =>
        this with { Slots = [.. Slots.Select(s => s.MemberId == e.MemberId ? s with { ClaimedBy = e.UserId } : s)] };

    public State Apply(ExpenseRecorded e) =>
        this with { Expenses = Expenses.SetItem(e.ExpenseId, new Expense(e.Description, e.AmountMinor, e.PayerMemberId, e.Split, e.Splits, e.PaidOn)) };

    public State Apply(ExpenseEdited e) =>
        !Expenses.ContainsKey(e.ExpenseId)
            ? this
            : this with { Expenses = Expenses.SetItem(e.ExpenseId, new Expense(e.Description, e.AmountMinor, e.PayerMemberId, e.Split, e.Splits, e.PaidOn)) };

    public State Apply(ExpenseRemoved e) => this with { Expenses = Expenses.Remove(e.ExpenseId) };
}
