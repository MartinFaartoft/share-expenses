using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.EditExpense;
using SplitIt.Slices.AddMember;
using SplitIt.Slices.RecordExpense;
using SplitIt.Slices.RecordSettlement;
using SplitIt.Slices.RemoveExpense;
using SplitIt.Slices.RenameGroup;

namespace SplitIt.Slices.ViewBalances;

/// <summary>A member slot as the fold keeps it.</summary>
/// <param name="ClaimedBy">The user holding the slot, if any.</param>
/// <param name="InvitedUntil">The current invite's recorded deadline, if any; whether it is still open is decided on reading.</param>
/// <param name="BalanceMinor">Paid minus shared, in minor units (spec §9): positive is owed, negative owes.</param>
internal sealed record Slot(MemberId MemberId, string Name, UserId? ClaimedBy, DateTimeOffset? InvitedUntil, long BalanceMinor);

/// <summary>What an expense did to the balances, kept so a removal can undo it.</summary>
internal sealed record Booked(MemberId PayerMemberId, long AmountMinor, IReadOnlyList<Split> Splits);

/// <summary>
/// What the balances are computed from, folded <em>live</em> from the group stream per
/// request — never stored (spec §11). Its own balances, not View group's: slices share
/// only events. A test checks the two agree.
///
/// <see cref="Slots"/> keep member-added order.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-08-view-balances.md:
///   SettlementRemoved                      → undo the entry's effect
///   MemberRenamed               → rename
///   MemberClaimReleased                    → clear ClaimedBy
///   MemberRemoved, GroupArchived           → decide how they show
///
/// The alias is required: every slice has a State (spec §12).
/// </summary>
[DocumentAlias("view_balances_state")]
internal sealed record State(
    string GroupName,
    string Currency,
    ImmutableList<Slot> Slots,
    ImmutableDictionary<ExpenseId, Booked> Expenses)
{
    /// <summary>The group's stream id.</summary>
    public Guid Id { get; init; }

    public static State Create(GroupCreated e) => new(e.Name, e.Currency, [], ImmutableDictionary<ExpenseId, Booked>.Empty);

    public State Apply(GroupRenamed e) => this with { GroupName = e.Name };

    public State Apply(MemberAdded e) =>
        this with { Slots = Slots.Add(new Slot(e.MemberId, e.DisplayName, null, null, 0)) };

    public State Apply(MemberInvited e) => WithSlot(e.MemberId, slot => slot with { InvitedUntil = e.ExpiresAt });

    public State Apply(MemberClaimed e) => WithSlot(e.MemberId, slot => slot with { ClaimedBy = e.UserId });

    public State Apply(ExpenseRecorded e)
    {
        // Spec §9: the payer is credited the amount; each sharer is debited their split.
        var debits = e.Splits.ToDictionary(s => s.MemberId, s => s.AmountMinor);
        return this with
        {
            Expenses = Expenses.SetItem(e.ExpenseId, new Booked(e.PayerMemberId, e.AmountMinor, e.Splits)),
            Slots =
            [
                .. Slots.Select(slot => slot with
                {
                    BalanceMinor = slot.BalanceMinor
                                   + (slot.MemberId == e.PayerMemberId ? e.AmountMinor : 0)
                                   - debits.GetValueOrDefault(slot.MemberId),
                }),
            ],
        };
    }

    public State Apply(ExpenseEdited e) =>
        !Expenses.ContainsKey(e.ExpenseId)
            ? this
            : Apply(new ExpenseRemoved(e.ExpenseId, e.By))
                .Apply(new ExpenseRecorded(e.ExpenseId, e.Description, e.AmountMinor, e.PayerMemberId, e.Split, e.Splits, e.PaidOn, e.By));

    public State Apply(ExpenseRemoved e)
    {
        if (!Expenses.TryGetValue(e.ExpenseId, out var booked))
            return this;

        var debits = booked.Splits.ToDictionary(s => s.MemberId, s => s.AmountMinor);
        return this with
        {
            Expenses = Expenses.Remove(e.ExpenseId),
            Slots =
            [
                .. Slots.Select(slot => slot with
                {
                    BalanceMinor = slot.BalanceMinor
                                   - (slot.MemberId == booked.PayerMemberId ? booked.AmountMinor : 0)
                                   + debits.GetValueOrDefault(slot.MemberId),
                }),
            ],
        };
    }

    public State Apply(SettlementRecorded e) =>
        // As an expense paid by `from` and shared by `to` alone (spec §7, §9).
        this with
        {
            Slots =
            [
                .. Slots.Select(slot => slot with
                {
                    BalanceMinor = slot.BalanceMinor
                                   + (slot.MemberId == e.FromMemberId ? e.AmountMinor : 0)
                                   - (slot.MemberId == e.ToMemberId ? e.AmountMinor : 0),
                }),
            ],
        };

    private State WithSlot(MemberId member, Func<Slot, Slot> change) =>
        this with { Slots = [.. Slots.Select(slot => slot.MemberId == member ? change(slot) : slot)] };
}
