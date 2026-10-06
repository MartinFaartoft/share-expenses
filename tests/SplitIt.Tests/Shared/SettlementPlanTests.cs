using SplitIt.Shared;

namespace SplitIt.Tests.Shared;

/// <summary>
/// The settle-up procedure (spec §10): slice-10-view-settlement-plan.md's procedure
/// scenarios, line for line, then the properties every plan must have over many
/// random balance sets.
/// </summary>
public class SettlementPlanTests
{
    private static readonly MemberId A = M(1), B = M(2), C = M(3), D = M(4), E = M(5);

    private static MemberId M(int n) => new(new Guid(n, 0, 0, new byte[8]));

    private static IReadOnlyList<Transfer> Plan(
        (MemberId Member, long Balance)[] balances, params (MemberId, MemberId)[] sharedOnce) =>
        SettlementPlan.Plan(balances, (x, y) => sharedOnce.Count(p => p == (x, y) || p == (y, x)));

    [Fact]
    public void P1_zero_balances_drop_out() =>
        Assert.Empty(Plan([(A, 0), (B, 0)]));

    [Fact]
    public void P2_one_debtor_pays_every_creditor() =>
        Assert.Equal([new Transfer(C, A, 2), new Transfer(C, B, 3)], Plan([(A, 2), (B, 3), (C, -5)]));

    [Fact]
    public void P3_exact_matches_are_taken_first() =>
        Assert.Equal([new Transfer(C, B, 5), new Transfer(D, A, 3)], Plan([(A, 3), (B, 5), (C, -5), (D, -3)]));

    [Fact]
    public void P4_several_exact_matches_the_higher_score_first() =>
        Assert.Equal(
            [new Transfer(C, B, 3), new Transfer(D, A, 3)],
            Plan([(A, 3), (B, 3), (C, -3), (D, -3)], (B, C), (A, D)));

    [Fact]
    public void P4_without_scores_member_added_order() =>
        Assert.Equal([new Transfer(C, A, 3), new Transfer(D, B, 3)], Plan([(A, 3), (B, 3), (C, -3), (D, -3)]));

    [Fact]
    public void P5_then_the_largest_creditor_with_the_largest_debtor() =>
        Assert.Equal(
            [new Transfer(C, A, 5), new Transfer(D, A, 1), new Transfer(D, B, 4)],
            Plan([(A, 6), (B, 4), (C, -5), (D, -5)]));

    [Fact]
    public void P6_equal_magnitudes_the_higher_score_wins_the_pairing() =>
        Assert.Equal(
            [new Transfer(C, A, 1), new Transfer(C, B, 4), new Transfer(D, A, 5)],
            Plan([(A, 6), (B, 4), (C, -5), (D, -5)], (A, D)));

    [Fact]
    public void Balances_that_do_not_sum_to_zero_are_refused() =>
        Assert.Throws<ArgumentException>(() => Plan([(A, 5), (B, -4)]));

    [Fact]
    public void Every_plan_clears_every_balance_in_at_most_n_minus_one_transfers()
    {
        var random = new Random(20261003);
        for (var run = 0; run < 5_000; run++)
        {
            var (balances, score) = RandomGroup(random);
            var plan = SettlementPlan.Plan(balances, score);

            // Every balance cleared exactly.
            var left = balances.ToDictionary(b => b.Member, b => b.Balance);
            foreach (var t in plan)
                (left[t.From], left[t.To]) = (left[t.From] + t.AmountMinor, left[t.To] - t.AmountMinor);
            Assert.All(left.Values, v => Assert.Equal(0, v));

            // At most n − 1, every one positive, debtor to creditor; nobody both pays and receives.
            var open = balances.Count(b => b.Balance != 0);
            Assert.True(plan.Count <= Math.Max(0, open - 1), $"{plan.Count} transfers for {open} open balances");
            var balance = balances.ToDictionary(b => b.Member, b => b.Balance);
            Assert.All(plan, t => Assert.True(t.AmountMinor > 0 && balance[t.From] < 0 && balance[t.To] > 0));
            Assert.Distinct(plan.Select(t => (t.From, t.To)));
        }
    }

    [Fact]
    public void Every_plan_takes_a_maximal_set_of_exact_matches()
    {
        var random = new Random(20261004);
        for (var run = 0; run < 2_000; run++)
        {
            var (balances, score) = RandomGroup(random, small: true);
            var plan = SettlementPlan.Plan(balances, score);
            var balance = balances.ToDictionary(b => b.Member, b => b.Balance);

            // A transfer of a debtor's whole debt to a creditor owed exactly that is an exact match.
            var matched = plan
                .Where(t => -balance[t.From] == balance[t.To] && t.AmountMinor == balance[t.To])
                .SelectMany(t => new[] { t.From, t.To })
                .ToHashSet();
            var untaken =
                from d in balance.Keys.Where(m => balance[m] < 0)
                from c in balance.Keys.Where(m => balance[m] > 0)
                where -balance[d] == balance[c] && !matched.Contains(d) && !matched.Contains(c)
                select (d, c);
            Assert.Empty(untaken);
        }
    }

    [Fact]
    public void The_same_balances_and_scores_always_give_the_same_plan()
    {
        var random = new Random(20261005);
        for (var run = 0; run < 500; run++)
        {
            var (balances, score) = RandomGroup(random, small: true);
            Assert.Equal(SettlementPlan.Plan(balances, score), SettlementPlan.Plan([.. balances], score));
        }
    }

    /// <summary>
    /// Up to 12 members with balances summing to zero, and random symmetric scores.
    /// Small amounts make equal magnitudes — exact matches and ties — common.
    /// </summary>
    private static ((MemberId Member, long Balance)[], Func<MemberId, MemberId, int>) RandomGroup(Random random, bool small = false)
    {
        var count = random.Next(1, 13);
        var members = Enumerable.Range(1, count).Select(M).ToArray();
        var balances = members.Select(m => random.NextInt64(small ? -5 : -1_000_000, small ? 6 : 1_000_001)).ToArray();
        balances[^1] -= balances.Sum();     // the last member absorbs the difference: the group sums to zero

        var scores = new Dictionary<(MemberId, MemberId), int>();
        foreach (var x in members)
            foreach (var y in members.Where(y => x.Value.CompareTo(y.Value) < 0))
                scores[(x, y)] = random.Next(3);
        int Score(MemberId x, MemberId y) =>
            scores.GetValueOrDefault((x, y), scores.GetValueOrDefault((y, x)));

        return ([.. members.Zip(balances)], Score);
    }
}
