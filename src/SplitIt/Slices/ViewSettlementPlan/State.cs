using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.EditExpense;
using SplitIt.Slices.RecordExpense;
using SplitIt.Slices.RecordSettlement;
using SplitIt.Slices.RemoveExpense;

namespace SplitIt.Slices.ViewSettlementPlan;

/// <summary>What an expense did to the balances and the score, kept so a removal can undo it.</summary>
internal sealed record Booked(MemberId PayerMemberId, long AmountMinor, IReadOnlyList<Split> Splits);

/// <summary>A member slot, with its balance: paid minus shared (spec §9).</summary>
internal sealed record Slot(MemberId MemberId, string Name, UserId? ClaimedBy, long BalanceMinor);

/// <summary>
/// What the settle-up plan is computed from, folded <em>live</em> from the group stream
/// per request — never stored (spec §10, §11). Its own balances, not the stored
/// ledger's: slices share only events. A test checks the two agree.
///
/// <see cref="Slots"/> keep member-added order, which breaks the last ties.
/// <see cref="SharedHistory"/> counts, per pair of members, the expenses both appear
/// in — as payer or in the split. Expenses only: a settlement is not shared spending.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-10-view-settlement-plan.md:
///   SettlementRemoved                  → undo the balance effect
///   MemberRenamed                      → rename
///   MemberClaimReleased                → clear ClaimedBy
///
/// The alias is required: every slice has a State (spec §12).
/// </summary>
[DocumentAlias("view_settlement_plan_state")]
internal sealed record State(
    string GroupName,
    string Currency,
    ImmutableList<Slot> Slots,
    ImmutableDictionary<(MemberId, MemberId), int> SharedHistory,
    ImmutableDictionary<ExpenseId, Booked> Expenses)
{
    /// <summary>The group's stream id.</summary>
    public Guid Id { get; init; }

    public static State Create(GroupCreated e) => new(e.Name, e.Currency, [], ImmutableDictionary<(MemberId, MemberId), int>.Empty, ImmutableDictionary<ExpenseId, Booked>.Empty);

    public State Apply(MemberAdded e) => this with { Slots = Slots.Add(new Slot(e.MemberId, e.DisplayName, null, 0)) };

    public State Apply(MemberClaimed e) =>
        this with { Slots = [.. Slots.Select(s => s.MemberId == e.MemberId ? s with { ClaimedBy = e.UserId } : s)] };

    public State Apply(ExpenseRecorded e)
    {
        var debits = e.Splits.ToDictionary(s => s.MemberId, s => s.AmountMinor);
        var slots = Slots.Select(s => s with
        {
            BalanceMinor = s.BalanceMinor + (s.MemberId == e.PayerMemberId ? e.AmountMinor : 0) - debits.GetValueOrDefault(s.MemberId),
        });

        return this with
        {
            Slots = [.. slots],
            SharedHistory = Shared(e.PayerMemberId, e.Splits, +1),
            Expenses = Expenses.SetItem(e.ExpenseId, new Booked(e.PayerMemberId, e.AmountMinor, e.Splits)),
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
        var slots = Slots.Select(s => s with
        {
            BalanceMinor = s.BalanceMinor
                           - (s.MemberId == booked.PayerMemberId ? booked.AmountMinor : 0)
                           + debits.GetValueOrDefault(s.MemberId),
        });

        return this with
        {
            Slots = [.. slots],
            SharedHistory = Shared(booked.PayerMemberId, booked.Splits, -1),
            Expenses = Expenses.Remove(e.ExpenseId),
        };
    }

    // Everyone in an expense — payer, and every member of the split, even at zero — shared it.
    private ImmutableDictionary<(MemberId, MemberId), int> Shared(MemberId payer, IReadOnlyList<Split> splits, int by)
    {
        var involved = splits.Select(s => s.MemberId).Append(payer).Distinct().ToList();
        var history = SharedHistory;
        for (var i = 0; i < involved.Count; i++)
            for (var j = i + 1; j < involved.Count; j++)
            {
                var pair = Key(involved[i], involved[j]);
                var score = history.GetValueOrDefault(pair) + by;
                history = score > 0 ? history.SetItem(pair, score) : history.Remove(pair);
            }

        return history;
    }

    public State Apply(SettlementRecorded e) => this with
    {
        Slots =
        [
            .. Slots.Select(s => s with
            {
                BalanceMinor = s.BalanceMinor
                               + (s.MemberId == e.FromMemberId ? e.AmountMinor : 0)
                               - (s.MemberId == e.ToMemberId ? e.AmountMinor : 0),
            }),
        ],
    };

    /// <summary>The shared-history score of a pair, either way round.</summary>
    public int Score(MemberId a, MemberId b) => SharedHistory.GetValueOrDefault(Key(a, b));

    /// <summary>A pair, in one canonical order, so (a, b) and (b, a) are one key.</summary>
    private static (MemberId, MemberId) Key(MemberId a, MemberId b) => a.Value.CompareTo(b.Value) <= 0 ? (a, b) : (b, a);
}
