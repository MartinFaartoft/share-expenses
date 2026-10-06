using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.RecordExpense;
using SplitIt.Slices.RecordSettlement;

namespace SplitIt.Slices.ViewSettlementPlan;

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
///   ExpenseRemoved, SettlementRemoved  → undo the balance effect (and the score)
///   the expense corrections            → undo, then redo
///   MemberRenamed                      → rename
///   MemberClaimReleased                → clear ClaimedBy
///
/// The alias is required: every slice has a State (spec §12).
/// </summary>
[DocumentAlias("view_settlement_plan_state")]
internal sealed record State(
    string Currency,
    ImmutableList<Slot> Slots,
    ImmutableDictionary<(MemberId, MemberId), int> SharedHistory)
{
    public static State Create(GroupCreated e) => new(e.Currency, [], ImmutableDictionary<(MemberId, MemberId), int>.Empty);

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

        // Everyone in it — payer, and every member of the split, even at zero — shared it.
        var involved = e.Splits.Select(s => s.MemberId).Append(e.PayerMemberId).Distinct().ToList();
        var history = SharedHistory;
        for (var i = 0; i < involved.Count; i++)
            for (var j = i + 1; j < involved.Count; j++)
            {
                var pair = Key(involved[i], involved[j]);
                history = history.SetItem(pair, history.GetValueOrDefault(pair) + 1);
            }

        return this with { Slots = [.. slots], SharedHistory = history };
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
