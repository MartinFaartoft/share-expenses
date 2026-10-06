using SplitIt.Shared;

namespace SplitIt.Tests.Shared;

/// <summary>
/// The rounding rule (spec §6). The slice specs pin worked examples; these pin the
/// properties every split must have, over many random inputs.
/// </summary>
public class SplitsTests
{
    private static readonly MemberId A = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId B = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId C = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly MemberId Outsider = new(Guid.Parse("00000000-0000-0000-0000-0000000000b9"));

    [Fact]
    public void Equal_gives_leftovers_to_the_payer_then_in_order() =>
        Assert.Equal(
            [new Split(A, 34), new Split(B, 33), new Split(C, 34)],
            Splits.SplitEqually(101, C, [A, B, C]));

    [Fact]
    public void Equal_skips_a_payer_who_does_not_share() =>
        Assert.Equal(
            [new Split(A, 34), new Split(B, 34), new Split(C, 33)],
            Splits.SplitEqually(101, Outsider, [A, B, C]));

    [Fact]
    public void Shares_give_leftovers_by_largest_remainder() =>
        // 100 × 1/6 = 16.67, × 2/6 = 33.33, × 3/6 = 50: one leftover, to the largest remainder.
        Assert.Equal(
            [new Split(A, 17), new Split(B, 33), new Split(C, 50)],
            Splits.SplitByShares(100, C, [(A, 1), (B, 2), (C, 3)]));

    [Fact]
    public void Shares_cannot_overflow()
    {
        var splits = Splits.SplitByShares(1_000_000_000_000, A, [(A, int.MaxValue), (B, int.MaxValue), (C, 1)]);

        Assert.Equal(1_000_000_000_000, splits.Sum(s => s.AmountMinor));
    }

    [Fact]
    public void Every_split_sums_to_the_total_and_is_within_one_minor_unit_of_its_fair_share()
    {
        var random = new Random(20261002);
        MemberId[] members = [.. Enumerable.Range(1, 12).Select(i => new MemberId(new Guid(i, 0, 0, new byte[8])))];

        for (var run = 0; run < 5_000; run++)
        {
            var total = random.NextInt64(1, 1_000_000_000_001);
            var participants = members.Take(random.Next(1, members.Length + 1)).ToList();
            var payer = members[random.Next(members.Length)];
            var shares = participants.Select(m => (m, random.Next(1, 1_000))).ToList();

            AssertFair(total, Splits.SplitEqually(total, payer, participants), participants.Select(m => (m, 1)).ToList());
            AssertFair(total, Splits.SplitByShares(total, payer, shares), shares);
        }
    }

    [Fact]
    public void The_same_input_always_gives_the_same_split() =>
        Assert.Equal(
            Splits.SplitByShares(997, B, [(A, 3), (B, 3), (C, 3)]),
            Splits.SplitByShares(997, B, [(A, 3), (B, 3), (C, 3)]));

    private static void AssertFair(long total, IReadOnlyList<Split> splits, List<(MemberId Member, int Share)> shares)
    {
        Assert.Equal(total, splits.Sum(s => s.AmountMinor));
        Assert.Equal(shares.Select(s => s.Member), splits.Select(s => s.MemberId));

        Int128 totalShares = shares.Sum(s => (long)s.Share);
        foreach (var (split, (_, share)) in splits.Zip(shares))
        {
            var floor = (long)((Int128)total * share / totalShares);
            Assert.InRange(split.AmountMinor, floor, floor + 1);
        }
    }
}
