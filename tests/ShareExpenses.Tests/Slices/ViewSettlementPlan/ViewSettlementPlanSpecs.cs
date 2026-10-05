using ShareExpenses.Shared;
using ShareExpenses.Slices.ViewSettlementPlan;
using ShareExpenses.Tests.Specs;
// Only the public events: tests can see every slice's internals, so namespace
// imports would bring in other slices' Command, State and Endpoint too.
using ExpenseRecorded = ShareExpenses.Slices.RecordExpense.ExpenseRecorded;
using GroupCreated = ShareExpenses.Slices.CreateGroup.GroupCreated;
using LedgerState = ShareExpenses.Slices.ViewBalances.State;
using MemberAdded = ShareExpenses.Slices.CreateGroup.MemberAdded;
using MemberClaimed = ShareExpenses.Slices.CreateGroup.MemberClaimed;
using MemberInvited = ShareExpenses.Slices.AddMember.MemberInvited;
using SettlementRecorded = ShareExpenses.Slices.RecordSettlement.SettlementRecorded;

namespace ShareExpenses.Tests.Slices.ViewSettlementPlan;

/// <summary>
/// docs/event-model/slice-10-view-settlement-plan.md, the slice scenarios, line for
/// line (the procedure scenarios are in <see cref="ShareExpenses.Tests.Shared.SettlementPlanTests"/>).
/// </summary>
public class ViewSettlementPlanSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly MemberId M4 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b4"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly UserId Bob = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));
    private static readonly UserId Mallory = new(Guid.Parse("00000000-0000-0000-0000-0000000000c9"));
    private static readonly ExpenseId E1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e1"));
    private static readonly ExpenseId E2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e2"));
    private static readonly SettlementId S1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000f1"));
    private static readonly DateOnly Oct1 = new(2026, 10, 1), Oct2 = new(2026, 10, 2);

    /// <summary>Alice and Bob joined; Carol a placeholder.</summary>
    private static readonly object[] Lisbon =
    [
        new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
        new MemberAdded(M1, "Alice", Alice),
        new MemberClaimed(M1, Alice),
        new MemberAdded(M2, "Bob", Alice),
        new MemberClaimed(M2, Bob),
        new MemberAdded(M3, "Carol", Alice),
    ];

    /// <summary>Scenario 2's dinner: Alice paid £90, split three ways.</summary>
    private static readonly ExpenseRecorded Dinner = Expense(E1, M1, 9000, (M1, 3000), (M2, 3000), (M3, 3000));

    private static ExpenseRecorded Expense(ExpenseId id, MemberId payer, long amount, params (MemberId Member, long Amount)[] splits) =>
        new(id, "Spend", amount, payer, new EqualSplit([.. splits.Select(s => s.Member)]),
            [.. splits.Select(s => new Split(s.Member, s.Amount))], Oct1, Alice);

    private static SettlementPlanReadModel? Read(IReadOnlyList<object> history, UserId user) =>
        Reader.Read(history.Count == 0 ? null : Fold.Of<State>(history, ignoring: typeof(MemberInvited)), new Query(user));

    /// <summary>The transfers as the .md writes them: "Bob → Alice 3000".</summary>
    private static string[] Transfers(IReadOnlyList<object> history) =>
        [.. Read(history, Alice)!.Transfers.Select(t => $"{t.FromName} → {t.ToName} {t.AmountMinor}")];

    [Fact]
    public void S1_nothing_owed_nothing_to_do()
    {
        var plan = Read(Lisbon, Alice)!;

        Assert.Equal(("GBP", M1), (plan.Currency, plan.You));
        Assert.Empty(plan.Transfers);
    }

    [Fact]
    public void S2_everyone_pays_back_the_payer() =>
        Assert.Equal(["Bob → Alice 3000", "Carol → Alice 3000"], Transfers([.. Lisbon, Dinner]));

    [Fact]
    public void S3_a_settlement_is_taken_into_account() =>
        Assert.Equal(["Carol → Alice 3000"], Transfers([.. Lisbon, Dinner, new SettlementRecorded(S1, M2, M1, 3000, Oct2, Bob)]));

    [Fact]
    public void S4_a_partial_settlement_leaves_the_rest() =>
        Assert.Equal(
            ["Bob → Alice 2000", "Carol → Alice 3000"],
            Transfers([.. Lisbon, Dinner, new SettlementRecorded(S1, M2, M1, 1000, Oct2, Bob)]));

    [Fact]
    public void S5_an_overpayment_is_paid_back_too() =>
        Assert.Equal(
            ["Carol → Alice 1000", "Carol → Bob 2000"],
            Transfers([.. Lisbon, Dinner, new SettlementRecorded(S1, M2, M1, 5000, Oct2, Bob)]));

    [Fact]
    public void S6_among_equal_amounts_people_who_shared_expenses_settle_with_each_other() =>
        Assert.Equal(
            ["Carol → Bob 3000", "Dave → Alice 3000"],
            Transfers(
            [
                .. Lisbon,
                new MemberAdded(M4, "Dave", Alice),
                Expense(E1, M2, 3000, (M3, 3000)),
                Expense(E2, M1, 3000, (M4, 3000)),
            ]));

    [Fact]
    public void S7_the_group_must_exist() =>
        Assert.Null(Read([], Alice));

    [Fact]
    public void S8_only_members_may_view() =>
        Assert.Null(Read(Lisbon, Mallory));

    [Fact]
    public void Transfers_carry_both_member_ids() =>
        Assert.Equal(
            new PlannedTransfer(M2, "Bob", M1, "Alice", 3000),
            Read([.. Lisbon, Dinner], Alice)!.Transfers[0]);

    [Fact]
    public void A_zero_exact_amount_still_counts_as_shared_history()
    {
        var state = Fold.Of<State>(
        [
            .. Lisbon,
            new ExpenseRecorded(E1, "Steak", 5000, M1, new ExactSplit([new MemberAmount(M1, 5000), new MemberAmount(M3, 0)]),
                [new Split(M1, 5000), new Split(M3, 0)], Oct1, Alice),
        ])!;

        Assert.Equal((1, 1), (state.Score(M1, M3), state.Score(M3, M1)));
        Assert.Equal(0, state.Score(M1, M2));
    }

    [Fact]
    public void A_settlement_is_not_shared_history() =>
        Assert.Equal(0, Fold.Of<State>([.. Lisbon, new SettlementRecorded(S1, M2, M1, 3000, Oct2, Bob)])!.Score(M1, M2));

    /// <summary>
    /// This slice folds its own balances; the stored ledger folds its own. Slices share
    /// only events, so nothing makes them agree but this: over random streams, they do.
    /// </summary>
    [Fact]
    public void Its_balances_agree_with_the_ledgers()
    {
        var random = new Random(20261006);
        MemberId[] slots = [M1, M2, M3];
        for (var run = 0; run < 500; run++)
        {
            List<object> history = [.. Lisbon];
            for (var i = 0; i < random.Next(1, 20); i++)
            {
                var payer = slots[random.Next(slots.Length)];
                var other = slots.First(m => m != payer);
                var amount = random.NextInt64(1, 1_000_000);
                history.Add(random.Next(3) == 0
                    ? new SettlementRecorded(SettlementId.New(), payer, other, amount, Oct1, Alice)
                    : Expense(ExpenseId.New(), payer, amount * 2, (payer, amount), (other, amount)));
            }

            var plan = Fold.Of<State>(history)!.Slots.Select(s => (s.MemberId, s.BalanceMinor));
            var ledger = Fold.Of<LedgerState>(history)!.Slots.Select(s => (s.MemberId, s.BalanceMinor));
            Assert.Equal(ledger, plan);
        }
    }
}
