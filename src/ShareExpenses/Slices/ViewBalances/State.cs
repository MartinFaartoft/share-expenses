using System.Collections.Immutable;
using Marten.Schema;
using ShareExpenses.Shared;
using ShareExpenses.Slices.CreateGroup;
using ShareExpenses.Slices.InviteMember;
using ShareExpenses.Slices.RecordExpense;

namespace ShareExpenses.Slices.ViewBalances;

/// <summary>A member slot as the ledger keeps it.</summary>
/// <param name="ClaimedBy">The user holding the slot, if any.</param>
/// <param name="InvitedUntil">The current invite's recorded deadline, if any; whether it is still open is decided on reading.</param>
/// <param name="BalanceMinor">Paid minus shared, in minor units (spec §9): positive is owed, negative owes.</param>
internal sealed record Slot(MemberId MemberId, string Name, UserId? ClaimedBy, DateTimeOffset? InvitedUntil, long BalanceMinor);

/// <summary>An expense as the ledger keeps it: the split as entered, and the amounts it produced.</summary>
internal sealed record Expense(
    ExpenseId ExpenseId,
    string Description,
    long AmountMinor,
    MemberId PayerMemberId,
    ExpenseSplit Split,
    IReadOnlyList<Split> Splits,
    DateOnly PaidOn);

/// <summary>
/// The group ledger: ViewBalances' state, and the first <em>stored</em> one — a Marten
/// snapshot projected <em>inline</em>, in the same transaction that appends the
/// events, as the <c>group_ledger</c> document (spec §11). So a balance moves the moment
/// an expense is saved.
///
/// It holds facts only. What depends on the reader ("you") or on the clock (whether an
/// invite is still open) is decided by <see cref="Reader"/>, never stored: a stored
/// document cannot change as time passes.
///
/// <see cref="Slots"/> keep member-added order; <see cref="Expenses"/> recorded order.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-07-view-balances.md:
///   SettlementRecorded / SettlementRemoved → move balances
///   ExpenseRemoved and the expense corrections → undo / redo an expense's effect
///   MemberRenamed / GroupRenamed           → rename
///   MemberClaimReleased                    → clear ClaimedBy
///   MemberRemoved, GroupArchived           → decide how they show
///
/// A change here changes stored documents: they must be rebuilt from the events
/// (<c>dotnet run -- projections rebuild</c>), which is always possible — the events are
/// the truth, the document a cache of them.
/// </summary>
[DocumentAlias("group_ledger")]
internal sealed record State(string GroupName, string Currency, ImmutableList<Slot> Slots, ImmutableList<Expense> Expenses)
{
    /// <summary>The group's stream id: Marten keys the stored snapshot by it.</summary>
    public Guid Id { get; init; }

    public static State Create(GroupCreated e) => new(e.Name, e.Currency, [], []);

    public State Apply(MemberAdded e) =>
        this with { Slots = Slots.Add(new Slot(e.MemberId, e.DisplayName, null, null, 0)) };

    public State Apply(MemberInvited e) => WithSlot(e.MemberId, slot => slot with { InvitedUntil = e.ExpiresAt });

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
            Expenses = Expenses.Add(new Expense(e.ExpenseId, e.Description, e.AmountMinor, e.PayerMemberId, e.Split, e.Splits, e.PaidOn)),
        };
    }

    private State WithSlot(MemberId member, Func<Slot, Slot> change) =>
        this with { Slots = [.. Slots.Select(slot => slot.MemberId == member ? change(slot) : slot)] };
}
