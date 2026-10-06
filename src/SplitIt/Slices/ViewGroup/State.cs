using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.RecordExpense;
using SplitIt.Slices.RecordSettlement;

namespace SplitIt.Slices.ViewGroup;

/// <summary>A member slot as the group activity keeps it.</summary>
/// <param name="ClaimedBy">The user holding the slot, if any.</param>
/// <param name="BalanceMinor">Paid minus shared, in minor units (spec §9): positive is owed, negative owes.</param>
internal sealed record Slot(MemberId MemberId, string Name, UserId? ClaimedBy, long BalanceMinor);

/// <summary>An expense as the ledger keeps it: the split as entered, and the amounts it produced.</summary>
/// <param name="Recorded">Its position among the group's expenses and settlements, in the order recorded.</param>
internal sealed record Expense(
    int Recorded,
    ExpenseId ExpenseId,
    string Description,
    long AmountMinor,
    MemberId PayerMemberId,
    ExpenseSplit Split,
    IReadOnlyList<Split> Splits,
    DateOnly PaidOn);

/// <summary>A settlement as the ledger keeps it.</summary>
/// <param name="Recorded">Its position among the group's expenses and settlements, in the order recorded.</param>
internal sealed record Settlement(
    int Recorded,
    SettlementId SettlementId,
    MemberId FromMemberId,
    MemberId ToMemberId,
    long AmountMinor,
    DateOnly PaidOn);

/// <summary>
/// The group activity: View group's state, a <em>stored</em> one — a Marten
/// snapshot projected <em>inline</em>, in the same transaction that appends the
/// events, as the <c>group_activity</c> document (spec §11). So an expense appears, and your standing
/// moves, the moment it is saved.
///
/// It holds facts only. What depends on the reader ("you", your standing) is decided
/// by <see cref="Reader"/>, never stored.
///
/// <see cref="Slots"/> keep member-added order. <see cref="Expenses"/> and
/// <see cref="Settlements"/> each record their position in the group's history, so the
/// reader can order the two as one.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-07-view-group.md:
///   ExpenseRemoved, SettlementRemoved      → undo the entry's effect
///   the expense corrections                → undo, then redo an expense's effect
///   MemberRenamed / GroupRenamed           → rename
///   MemberClaimReleased                    → clear ClaimedBy
///   MemberRemoved, GroupArchived           → decide how they show
///
/// A change here changes stored documents: they must be rebuilt from the events
/// (<c>dotnet run -- projections rebuild</c>), which is always possible — the events are
/// the truth, the document a cache of them.
/// </summary>
[DocumentAlias("group_activity")]
internal sealed record State(
    string GroupName,
    string Currency,
    ImmutableList<Slot> Slots,
    ImmutableList<Expense> Expenses,
    ImmutableList<Settlement> Settlements)
{
    /// <summary>The group's stream id: Marten keys the stored snapshot by it.</summary>
    public Guid Id { get; init; }

    public static State Create(GroupCreated e) => new(e.Name, e.Currency, [], [], []);

    public State Apply(MemberAdded e) =>
        this with { Slots = Slots.Add(new Slot(e.MemberId, e.DisplayName, null, 0)) };

    public State Apply(MemberClaimed e) => WithSlot(e.MemberId, slot => slot with { ClaimedBy = e.UserId });

    public State Apply(ExpenseRecorded e)
    {
        // Spec §9: the payer is credited the amount; each sharer is debited their split.
        var debits = e.Splits.ToDictionary(s => s.MemberId, s => s.AmountMinor);
        var slots = Slots.Select(slot => slot with
        {
            BalanceMinor = slot.BalanceMinor
                           + (slot.MemberId == e.PayerMemberId ? e.AmountMinor : 0)
                           - debits.GetValueOrDefault(slot.MemberId),
        });

        return this with
        {
            Slots = [.. slots],
            Expenses = Expenses.Add(new Expense(
                Entries, e.ExpenseId, e.Description, e.AmountMinor, e.PayerMemberId, e.Split, e.Splits, e.PaidOn)),
        };
    }

    public State Apply(SettlementRecorded e)
    {
        // As an expense paid by `from` and shared by `to` alone (spec §7, §9).
        var slots = Slots.Select(slot => slot with
        {
            BalanceMinor = slot.BalanceMinor
                           + (slot.MemberId == e.FromMemberId ? e.AmountMinor : 0)
                           - (slot.MemberId == e.ToMemberId ? e.AmountMinor : 0),
        });

        return this with
        {
            Slots = [.. slots],
            Settlements = Settlements.Add(new Settlement(
                Entries, e.SettlementId, e.FromMemberId, e.ToMemberId, e.AmountMinor, e.PaidOn)),
        };
    }

    /// <summary>How many expenses and settlements have been recorded: the next one's position.</summary>
    private int Entries => Expenses.Count + Settlements.Count;

    private State WithSlot(MemberId member, Func<Slot, Slot> change) =>
        this with { Slots = [.. Slots.Select(slot => slot.MemberId == member ? change(slot) : slot)] };
}
