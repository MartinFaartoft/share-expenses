using SplitIt.Shared;
using SplitIt.Slices.ViewBalances;
using SplitIt.Tests.Specs;
// Only the public events: tests can see every slice's internals, so namespace
// imports would bring in other slices' Command, State and Endpoint too.
using ExpenseRecorded = SplitIt.Slices.RecordExpense.ExpenseRecorded;
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;
using MemberInvited = SplitIt.Slices.AddMember.MemberInvited;
using RecordExpenseCommand = SplitIt.Slices.RecordExpense.Command;
using RecordExpenseDecider = SplitIt.Slices.RecordExpense.Decider;
using RecordExpenseState = SplitIt.Slices.RecordExpense.State;
using SettlementRecorded = SplitIt.Slices.RecordSettlement.SettlementRecorded;

namespace SplitIt.Tests.Slices.ViewBalances;

/// <summary>
/// docs/event-model/slice-08-view-balances.md, line for line, against the fold and the
/// pure read. The same fold runs, through HTTP, in
/// <see cref="ViewBalancesIntegrationTests"/>.
/// </summary>
public class ViewBalancesSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly UserId Bob = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));
    private static readonly UserId Carol = new(Guid.Parse("00000000-0000-0000-0000-0000000000c3"));
    private static readonly UserId Mallory = new(Guid.Parse("00000000-0000-0000-0000-0000000000c9"));
    private static readonly InviteId I1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000d1"));
    private static readonly ExpenseId E1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e1"));
    private static readonly ExpenseId E2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e2"));
    private static readonly ExpenseId E3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e3"));
    private static readonly SettlementId S1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000f1"));

    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Oct1 = new(2026, 10, 1);

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

    private static readonly Query AsAlice = new(Alice, T0);

    private static GroupBalancesReadModel? Read(IReadOnlyList<object> history, Query query) =>
        Reader.Read(history.Count == 0 ? null : Fold.Of<State>(history), query);

    private static GroupBalancesReadModel View(IReadOnlyList<object> history, Query? query = null) =>
        Read(history, query ?? AsAlice) ?? throw new Xunit.Sdk.XunitException("Expected the balances, but found nothing");

    /// <summary>Members as the .md writes them: name, status, balance.</summary>
    private static (string, string, long)[] Members(GroupBalancesReadModel balances) =>
        [.. balances.Members.Select(m => (m.Name, m.Status, m.BalanceMinor))];

    private static ExpenseRecorded Expense(
        ExpenseId id, string description, long amount, MemberId payer, (MemberId Member, long Amount)[] splits,
        DateOnly? paidOn = null) =>
        new(id, description, amount, payer, new EqualSplit([.. splits.Select(s => s.Member)]),
            [.. splits.Select(s => new Split(s.Member, s.Amount))], paidOn ?? Oct1, Alice);

    [Fact]
    public void S1_a_group_with_no_expenses_everyone_at_zero()
    {
        var balances = View(Lisbon);

        Assert.Equal(("Lisbon trip", "GBP", M1), (balances.GroupName, balances.Currency, balances.You));
        Assert.Equal([("Alice", "joined", 0L), ("Bob", "joined", 0L), ("Carol", "placeholder", 0L)], Members(balances));
    }

    [Fact]
    public void S2_an_expense_moves_balances_the_payer_is_owed_the_sharers_owe()
    {
        var balances = View([.. Lisbon, Expense(E1, "Dinner", 9000, M1, [(M1, 3000), (M2, 3000), (M3, 3000)])]);

        Assert.Equal([("Alice", "joined", 6000L), ("Bob", "joined", -3000L), ("Carol", "placeholder", -3000L)], Members(balances));
    }

    [Fact]
    public void S3_a_payer_who_does_not_share_is_owed_it_all() =>
        Assert.Equal(
            [("Alice", "joined", 4500L), ("Bob", "joined", -4500L), ("Carol", "placeholder", 0L)],
            Members(View([.. Lisbon, Expense(E1, "Bob's ticket", 4500, M1, [(M2, 4500)])])));

    [Fact]
    public void S4_balances_add_up_across_expenses_and_always_to_zero()
    {
        var balances = View(
        [
            .. Lisbon,
            Expense(E1, "Dinner", 9000, M1, [(M1, 3000), (M2, 3000), (M3, 3000)]),
            Expense(E2, "Taxi", 3000, M2, [(M1, 1500), (M2, 1500)]),
        ]);

        Assert.Equal([("Alice", "joined", 4500L), ("Bob", "joined", -1500L), ("Carol", "placeholder", -3000L)], Members(balances));
        Assert.Equal(0, balances.Members.Sum(m => m.BalanceMinor));
    }

    [Fact]
    public void S6_an_invited_slot_shows_as_invited_until_its_deadline() =>
        Assert.Equal(
            [("Alice", "joined", 0L), ("Bob", "joined", 0L), ("Carol", "invited", 0L)],
            Members(View([.. Lisbon, new MemberInvited(M3, I1, T0.AddDays(1), Alice)])));

    [Fact]
    public void S7_and_as_a_placeholder_again_after_it() =>
        Assert.Equal(
            [("Alice", "joined", 0L), ("Bob", "joined", 0L), ("Carol", "placeholder", 0L)],
            Members(View([.. Lisbon, new MemberInvited(M3, I1, T0, Alice)])));

    [Fact]
    public void S8_a_claimed_slot_has_joined_whatever_its_invite() =>
        Assert.Equal(
            [("Alice", "joined", 0L), ("Bob", "joined", 0L), ("Carol", "joined", 0L)],
            Members(View([.. Lisbon, new MemberInvited(M3, I1, T0.AddDays(1), Alice), new MemberClaimed(M3, Carol)])));

    [Fact]
    public void S9_you_is_the_callers_own_slot() =>
        Assert.Equal(M2, View(Lisbon, new Query(Bob, T0)).You);

    [Fact]
    public void S10_the_group_must_exist() =>
        Assert.Null(Read([], AsAlice));

    [Fact]
    public void S11_only_members_may_view() =>
        Assert.Null(Read(Lisbon, new Query(Mallory, T0)));

    [Fact]
    public void S5_a_settlement_moves_balances_the_payers_up_the_recipients_down() =>
        Assert.Equal(
            [("Alice", "joined", 3000L), ("Bob", "joined", 0L), ("Carol", "placeholder", -3000L)],
            Members(View(
            [
                .. Lisbon,
                Expense(E1, "Dinner", 9000, M1, [(M1, 3000), (M2, 3000), (M3, 3000)]),
                new SettlementRecorded(S1, M2, M1, 3000, new DateOnly(2026, 10, 2), Bob),
            ])));

    [Fact]
    public void An_unclaimed_placeholder_confers_no_access() =>
        // Carol's slot exists, but no user holds it: Carol herself is not a member yet.
        Assert.Null(Read([.. Lisbon, new MemberInvited(M3, I1, T0.AddDays(1), Alice)], new Query(Carol, T0)));

    /// <summary>Spec §9: balances always sum to zero — over random streams of expenses and settlements.</summary>
    [Fact]
    public void Balances_always_sum_to_zero()
    {
        var random = new Random(20261002);
        MemberId[] slots = [M1, M2, M3];

        for (var run = 0; run < 500; run++)
        {
            List<object> history = [.. Lisbon];
            for (var i = 0; i < random.Next(1, 20); i++)
            {
                var sharers = slots.Where(_ => random.Next(2) == 0).DefaultIfEmpty(M1).ToList();
                var total = random.NextInt64(1, 1_000_000_000_000);
                ExpenseSplit split = random.Next(3) switch
                {
                    0 => new EqualSplit(sharers),
                    1 => new SharesSplit([.. sharers.Select(m => new MemberShares(m, random.Next(1, 10)))]),
                    _ => new ExactSplit([.. sharers.Select((m, j) => new MemberAmount(m, j == 0 ? total : 0))]),
                };
                var command = new RecordExpenseCommand(
                    ExpenseId.New(), "Spend", total, slots[random.Next(slots.Length)], split, Oct1, T0, Alice);
                // Recorded through RecordExpense's own decider, so every split is one it would accept.
                var decision = RecordExpenseDecider.Decide(
                    Fold.Of<RecordExpenseState>(history, ignoring: [typeof(ExpenseRecorded), typeof(SettlementRecorded)]), command);
                history.AddRange(Assert.IsType<Decision.Accepted>(decision).Events);

                // And now and then a payment between two members, of any size.
                if (random.Next(3) == 0)
                {
                    var from = slots[random.Next(slots.Length)];
                    var to = slots.First(m => m != from);
                    history.Add(new SettlementRecorded(SettlementId.New(), from, to, random.NextInt64(1, total + 1), Oct1, Alice));
                }
            }

            Assert.Equal(0, View(history).Members.Sum(m => m.BalanceMinor));
        }
    }

    /// <summary>
    /// Two slices fold balances, each its own (slices share only events): over random
    /// streams, View group's standing for each member is View balances' balance.
    /// </summary>
    [Fact]
    public void Balances_agree_with_each_members_standing_in_view_group()
    {
        var random = new Random(20261004);
        (MemberId Slot, UserId User)[] members = [(M1, Alice), (M2, Bob)];
        MemberId[] slots = [M1, M2, M3];

        for (var run = 0; run < 300; run++)
        {
            List<object> history = [.. Lisbon];
            for (var i = 0; i < random.Next(1, 20); i++)
            {
                var sharers = slots.Where(_ => random.Next(2) == 0).DefaultIfEmpty(M1).ToList();
                var total = random.NextInt64(1, 1_000_000);
                var amounts = sharers.Select((m, j) => new Split(m, j == 0 ? total : 0)).ToList();
                history.Add(new ExpenseRecorded(
                    ExpenseId.New(), "Spend", total, slots[random.Next(slots.Length)],
                    new EqualSplit(sharers), amounts, Oct1, Alice));
                if (random.Next(3) == 0)
                {
                    var from = slots[random.Next(slots.Length)];
                    history.Add(new SettlementRecorded(
                        SettlementId.New(), from, slots.First(m => m != from), random.NextInt64(1, total + 1), Oct1, Alice));
                }
            }

            var balances = View(history).Members.ToDictionary(m => m.MemberId, m => m.BalanceMinor);
            foreach (var (slot, user) in members)
            {
                var group = SplitIt.Slices.ViewGroup.Reader.Read(
                    Fold.Of<SplitIt.Slices.ViewGroup.State>(history), new SplitIt.Slices.ViewGroup.Query(user));
                Assert.Equal(balances[slot], group!.BalanceMinor);
            }
        }
    }
}
