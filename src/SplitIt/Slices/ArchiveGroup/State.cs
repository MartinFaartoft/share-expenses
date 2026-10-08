using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.EditExpense;
using SplitIt.Slices.RecordExpense;
using SplitIt.Slices.RecordSettlement;
using SplitIt.Slices.RemoveExpense;
using SplitIt.Slices.RenameGroup;

namespace SplitIt.Slices.ArchiveGroup;

/// <summary>A member slot as the fold keeps it.</summary>
/// <param name="BalanceMinor">Paid minus shared, in minor units (spec §9): positive is owed, negative owes.</param>
public sealed record Slot(MemberId MemberId, string Name, long BalanceMinor);

/// <summary>What an expense did to the balances, kept so an edit or a removal can undo it.</summary>
public sealed record Booked(MemberId PayerMemberId, long AmountMinor, IReadOnlyList<Split> Splits);

/// <summary>
/// What ArchiveGroup needs to know about a group: who may archive, whether it already is,
/// and — for the page's warning, never for deciding — where everyone stands (spec §9).
/// Its own balances, as View balances and View settlement plan each have theirs: slices share
/// only events.
///
/// <see cref="Slots"/> keep member-added order, in which the warning lists them.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-15-archive-group.md:
///   MemberClaimReleased → remove from Members        (a released claim ends membership)
///   SettlementRemoved   → undo the entry's effect
///   MemberRenamed       → rename the slot
///   MemberRemoved       → decide how it shows
///   GroupUnarchived     → clear Archived             (deferred, spec §11)
///
/// Public: Wolverine fetches it for the endpoint (spec §12). The alias is required:
/// every slice has a State.
/// </summary>
[DocumentAlias("archive_group_state")]
public sealed record State(
    string GroupName,
    string Currency,
    ImmutableList<Slot> Slots,
    ImmutableDictionary<UserId, MemberId> Members,
    ImmutableDictionary<ExpenseId, Booked> Expenses,
    bool Archived)
{
    /// <summary>The stream id, set by Marten; Wolverine needs it to type the aggregate's identity.</summary>
    public GroupId Id { get; init; }

    public static State Create(GroupCreated e) =>
        new(e.Name, e.Currency, [], ImmutableDictionary<UserId, MemberId>.Empty, ImmutableDictionary<ExpenseId, Booked>.Empty, false);

    public State Apply(GroupRenamed e) => this with { GroupName = e.Name };

    public State Apply(MemberAdded e) => this with { Slots = Slots.Add(new Slot(e.MemberId, e.DisplayName, 0)) };

    public State Apply(MemberClaimed e) => this with { Members = Members.SetItem(e.UserId, e.MemberId) };

    public State Apply(ExpenseRecorded e) => Book(e.ExpenseId, e.PayerMemberId, e.AmountMinor, e.Splits);

    public State Apply(ExpenseEdited e) =>
        Expenses.ContainsKey(e.ExpenseId) ? Unbook(e.ExpenseId).Book(e.ExpenseId, e.PayerMemberId, e.AmountMinor, e.Splits) : this;

    public State Apply(ExpenseRemoved e) => Expenses.ContainsKey(e.ExpenseId) ? Unbook(e.ExpenseId) : this;

    public State Apply(SettlementRecorded e) =>
        // As an expense paid by `from` and shared by `to` alone (spec §7, §9).
        Move(e.FromMemberId, +e.AmountMinor).Move(e.ToMemberId, -e.AmountMinor);

    public State Apply(GroupArchived e) => this with { Archived = true };

    // Spec §9: the payer is credited the amount; each sharer is debited their split.
    private State Book(ExpenseId id, MemberId payer, long amount, IReadOnlyList<Split> splits) =>
        splits.Aggregate(Move(payer, +amount), (state, split) => state.Move(split.MemberId, -split.AmountMinor)) with
        {
            Expenses = Expenses.SetItem(id, new Booked(payer, amount, splits)),
        };

    private State Unbook(ExpenseId id)
    {
        var booked = Expenses[id];
        return booked.Splits.Aggregate(Move(booked.PayerMemberId, -booked.AmountMinor), (state, split) => state.Move(split.MemberId, +split.AmountMinor)) with
        {
            Expenses = Expenses.Remove(id),
        };
    }

    private State Move(MemberId member, long by) =>
        this with { Slots = [.. Slots.Select(slot => slot.MemberId == member ? slot with { BalanceMinor = slot.BalanceMinor + by } : slot)] };
}
