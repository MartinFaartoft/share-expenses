using SplitIt.Shared;
using SplitIt.Slices.RecordExpense;
using SplitIt.Tests.Specs;
// Only the public events: tests can see every slice's internals, so a namespace
// import would bring in CreateGroup's own Command too.
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;
using GroupRenamed = SplitIt.Slices.RenameGroup.GroupRenamed;
using GroupDefaultSplitChanged = SplitIt.Slices.ChangeDefaultSplit.GroupDefaultSplitChanged;

namespace SplitIt.Tests.Slices.RecordExpense;

/// <summary>
/// docs/event-model/slice-06-record-expense.md, line for line. Selected scenarios run
/// against a real store, through HTTP, in <see cref="RecordExpenseIntegrationTests"/>.
/// </summary>
public class RecordExpenseSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly MemberId M9 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b9"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly UserId Bob = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));
    private static readonly UserId Mallory = new(Guid.Parse("00000000-0000-0000-0000-0000000000c9"));
    private static readonly ExpenseId E1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e1"));

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
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

    private static readonly DecideSpec<Command> Spec =
        new((history, command) => Decider.Decide(Fold.Of<State>(history), command));

    // ── notation, as in the .md ───────────────────────────────────────────────────

    private static Command RecordExpense(
        string? description, long amount, MemberId payer, ExpenseSplit? split, UserId by, DateOnly? paidOn = null) =>
        new(E1, description, amount, payer, split, paidOn ?? Oct1, Now, by);

    private static EqualSplit Equal(params MemberId[] members) => new(members);

    private static SharesSplit Shares(params (MemberId Member, int Shares)[] shares) =>
        new([.. shares.Select(s => new MemberShares(s.Member, s.Shares))]);

    private static ExactSplit Exact(params (MemberId Member, long Amount)[] amounts) =>
        new([.. amounts.Select(a => new MemberAmount(a.Member, a.Amount))]);

    private static Split[] Splits(params (MemberId Member, long Amount)[] splits) =>
        [.. splits.Select(s => new Split(s.Member, s.Amount))];

    private static ExpenseRecorded Recorded(
        string description, long amount, MemberId payer, ExpenseSplit split, Split[] splits, UserId by,
        DateOnly? paidOn = null) =>
        new(E1, description, amount, payer, split, splits, paidOn ?? Oct1, by);

    /// <summary>Compares the amounts by content: the event's list is not a record itself.</summary>
    private static void ThenRecorded(Command command, ExpenseRecorded expected)
    {
        var actual = Accepted(command);
        Assert.Equal(expected with { Splits = [] }, actual with { Splits = [] });
        Assert.Equal(expected.Splits, actual.Splits);
    }

    private static void ThenSplits(Command command, params (MemberId Member, long Amount)[] splits) =>
        Assert.Equal(Splits(splits), Accepted(command).Splits);

    private static ExpenseRecorded Accepted(Command command) =>
        Assert.IsType<ExpenseRecorded>(Assert.Single(
            Assert.IsType<Decision.Accepted>(Decider.Decide(Fold.Of<State>(Lisbon), command)).Events));

    // ── scenarios ─────────────────────────────────────────────────────────────────

    [Fact]
    public void S1_records_an_equal_split() =>
        ThenRecorded(
            RecordExpense("Dinner", 9000, M1, Equal(M1, M2, M3), Alice),
            Recorded("Dinner", 9000, M1, Equal(M1, M2, M3), Splits((M1, 3000), (M2, 3000), (M3, 3000)), Alice));

    [Fact]
    public void S2_a_placeholder_can_pay_and_share() =>
        ThenRecorded(
            RecordExpense("Taxi", 3000, M3, Equal(M2, M3), Alice),
            Recorded("Taxi", 3000, M3, Equal(M2, M3), Splits((M2, 1500), (M3, 1500)), Alice));

    [Fact]
    public void S3_the_payer_need_not_share() =>
        ThenRecorded(
            RecordExpense("Bob's ticket", 4500, M1, Equal(M2), Alice),
            Recorded("Bob's ticket", 4500, M1, Equal(M2), Splits((M2, 4500)), Alice));

    [Fact]
    public void S4_an_equal_splits_leftover_goes_to_the_payer_first() =>
        ThenSplits(RecordExpense("Coffee", 1000, M2, Equal(M1, M2, M3), Alice), (M1, 333), (M2, 334), (M3, 333));

    [Fact]
    public void S5_then_in_member_added_order() =>
        ThenSplits(RecordExpense("Coffee", 1001, M1, Equal(M1, M2, M3), Alice), (M1, 334), (M2, 334), (M3, 333));

    [Fact]
    public void S6_and_the_payer_is_skipped_when_not_sharing() =>
        ThenSplits(RecordExpense("Coffee", 1001, M1, Equal(M2, M3), Alice), (M2, 501), (M3, 500));

    [Fact]
    public void S7_records_a_shares_split() =>
        ThenRecorded(
            RecordExpense("Flat", 1000, M1, Shares((M1, 2), (M2, 1), (M3, 1)), Alice),
            Recorded("Flat", 1000, M1, Shares((M1, 2), (M2, 1), (M3, 1)), Splits((M1, 500), (M2, 250), (M3, 250)), Alice));

    [Fact]
    public void S8_a_shares_splits_leftovers_go_by_largest_remainder() =>
        ThenSplits(RecordExpense("Flat", 100, M1, Shares((M1, 1), (M2, 2)), Alice), (M1, 33), (M2, 67));

    [Fact]
    public void S9_equal_remainders_payer_first_then_member_added_order() =>
        ThenSplits(RecordExpense("Flat", 1000, M3, Shares((M1, 1), (M2, 1), (M3, 1)), Alice), (M1, 333), (M2, 333), (M3, 334));

    [Fact]
    public void S10_records_an_exact_split() =>
        ThenRecorded(
            RecordExpense("Steak night", 5000, M1, Exact((M1, 2000), (M2, 3000)), Alice),
            Recorded("Steak night", 5000, M1, Exact((M1, 2000), (M2, 3000)), Splits((M1, 2000), (M2, 3000)), Alice));

    [Fact]
    public void S11_exact_amounts_must_add_up_to_the_total() =>
        Spec.Given(Lisbon)
            .When(RecordExpense("Steak night", 5000, M1, Exact((M1, 2000), (M2, 2999)), Alice))
            .ThenRejected("exact amounts must add up to the total");

    [Fact]
    public void S12_the_split_is_recorded_in_member_added_order() =>
        ThenRecorded(
            RecordExpense("Dinner", 9000, M1, Equal(M3, M1, M2), Alice),
            Recorded("Dinner", 9000, M1, Equal(M1, M2, M3), Splits((M1, 3000), (M2, 3000), (M3, 3000)), Alice));

    [Fact]
    public void S12_in_every_mode() =>
        ThenRecorded(
            RecordExpense("Steak night", 5000, M1, Exact((M2, 3000), (M1, 2000)), Alice),
            Recorded("Steak night", 5000, M1, Exact((M1, 2000), (M2, 3000)), Splits((M1, 2000), (M2, 3000)), Alice));

    [Fact]
    public void S13_any_member_may_record_and_is_recorded_as_the_actor() =>
        ThenRecorded(
            RecordExpense("Dinner", 9000, M1, Equal(M1, M2), Bob),
            Recorded("Dinner", 9000, M1, Equal(M1, M2), Splits((M1, 4500), (M2, 4500)), Bob));

    [Fact]
    public void S14_the_group_must_exist() =>
        Spec.Given()
            .When(RecordExpense("Dinner", 9000, M1, Equal(M1), Alice))
            .ThenNotFound("group not found");

    [Fact]
    public void S15_only_members_of_the_group_may_record() =>
        Spec.Given(Lisbon)
            .When(RecordExpense("Dinner", 9000, M1, Equal(M1), Mallory))
            .ThenNotFound("group not found");

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void S16_a_description_is_required(string? description) =>
        Spec.Given(Lisbon)
            .When(RecordExpense(description, 9000, M1, Equal(M1), Alice))
            .ThenRejected("description is required");

    [Fact]
    public void S17_a_description_is_at_most_100_characters() =>
        Spec.Given(Lisbon)
            .When(RecordExpense(new string('d', 101), 9000, M1, Equal(M1), Alice))
            .ThenRejected("description must be at most 100 characters");

    [Fact]
    public void S17_and_100_after_trimming_is_fine() =>
        ThenSplits(RecordExpense("  " + new string('d', 100) + " ", 9000, M1, Equal(M1), Alice), (M1, 9000));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void S18_the_amount_must_be_positive(long amount) =>
        Spec.Given(Lisbon)
            .When(RecordExpense("Dinner", amount, M1, Equal(M1), Alice))
            .ThenRejected("amount must be positive");

    [Fact]
    public void S19_the_amount_has_a_ceiling() =>
        Spec.Given(Lisbon)
            .When(RecordExpense("Dinner", 1_000_000_000_001, M1, Equal(M1), Alice))
            .ThenRejected("amount is too large");

    [Fact]
    public void S19_and_the_ceiling_itself_is_fine() =>
        ThenSplits(RecordExpense("Dinner", 1_000_000_000_000, M1, Equal(M1, M2), Alice), (M1, 500_000_000_000), (M2, 500_000_000_000));

    [Fact]
    public void S20_the_payer_must_be_a_member_slot_of_the_group() =>
        Spec.Given(Lisbon)
            .When(RecordExpense("Dinner", 9000, M9, Equal(M1), Alice))
            .ThenRejected("payer is not a member of the group");

    [Fact]
    public void S20_a_missing_payer_is_not_a_member() =>
        Spec.Given(Lisbon)
            .When(new Command(E1, "Dinner", 9000, null, Equal(M1), Oct1, Now, Alice))
            .ThenRejected("payer is not a member of the group");

    [Fact]
    public void S21_a_split_is_required() =>
        Spec.Given(Lisbon)
            .When(RecordExpense("Dinner", 9000, M1, null, Alice))
            .ThenRejected("a split is required");

    [Fact]
    public void S22_every_participant_must_be_a_member_slot_of_the_group() =>
        Spec.Given(Lisbon)
            .When(RecordExpense("Dinner", 9000, M1, Equal(M1, M9), Alice))
            .ThenRejected("participant is not a member of the group");

    [Fact]
    public void S22_in_any_mode() =>
        Spec.Given(Lisbon)
            .When(RecordExpense("Flat", 1000, M1, Shares((M9, 1)), Alice))
            .ThenRejected("participant is not a member of the group");

    [Fact]
    public void S23_someone_must_share_it() =>
        Spec.Given(Lisbon)
            .When(RecordExpense("Dinner", 9000, M1, Shares(), Alice))
            .ThenRejected("at least one participant is required");

    [Fact]
    public void S24_nobody_shares_it_twice() =>
        Spec.Given(Lisbon)
            .When(RecordExpense("Steak night", 5000, M1, Exact((M1, 2500), (M2, 2000), (M1, 500)), Alice))
            .ThenRejected("a participant appears more than once");

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void S25_every_share_is_a_positive_whole_number(int share) =>
        Spec.Given(Lisbon)
            .When(RecordExpense("Flat", 1000, M1, Shares((M1, 2), (M2, share)), Alice))
            .ThenRejected("every share must be a positive whole number");

    [Fact]
    public void S26_no_exact_amount_is_negative() =>
        Spec.Given(Lisbon)
            .When(RecordExpense("Steak night", 5000, M1, Exact((M1, 6000), (M2, -1000)), Alice))
            .ThenRejected("every exact amount must be zero or more");

    [Fact]
    public void S26_but_zero_is_fine() =>
        ThenSplits(RecordExpense("Steak night", 5000, M1, Exact((M1, 5000), (M2, 0)), Alice), (M1, 5000), (M2, 0));

    [Fact]
    public void S27_the_date_may_be_tomorrow_for_time_zones() =>
        ThenRecorded(
            RecordExpense("Dinner", 9000, M1, Equal(M1), Alice, paidOn: new DateOnly(2026, 10, 3)),
            Recorded("Dinner", 9000, M1, Equal(M1), Splits((M1, 9000)), Alice, paidOn: new DateOnly(2026, 10, 3)));

    [Fact]
    public void S28_but_no_later() =>
        Spec.Given(Lisbon)
            .When(RecordExpense("Dinner", 9000, M1, Equal(M1), Alice, paidOn: new DateOnly(2026, 10, 4)))
            .ThenRejected("date cannot be in the future");

    [Fact]
    public void S29_and_any_earlier_date_is_fine_before_the_group_too() =>
        ThenRecorded(
            RecordExpense("Flights", 60000, M1, Equal(M1, M2), Alice, paidOn: new DateOnly(2026, 6, 15)),
            Recorded("Flights", 60000, M1, Equal(M1, M2), Splits((M1, 30000), (M2, 30000)), Alice,
                paidOn: new DateOnly(2026, 6, 15)));

    [Fact]
    public void S30_a_date_is_required() =>
        Spec.Given(Lisbon)
            .When(new Command(E1, "Dinner", 9000, M1, Equal(M1), null, Now, Alice))
            .ThenRejected("date is required");

    [Fact]
    public void S31_the_amount_must_be_readable() =>
        Spec.Given(Lisbon)
            .When(new Command(E1, "Dinner", null, M1, Equal(M1), Oct1, Now, Alice))
            .ThenRejected("amount must be a number");

    [Fact]
    public void S32_an_expense_id_already_recorded_is_not_recorded_twice() =>
        Spec.Given([.. Lisbon, Recorded("Dinner", 9000, M1, Equal(M1, M2, M3), Splits((M1, 3000), (M2, 3000), (M3, 3000)), Alice)])
            .When(RecordExpense("Dinner", 9000, M1, Equal(M1, M2, M3), Alice))
            .ThenAlreadyRecorded("expense already recorded");

    [Fact]
    public void A_non_member_learns_nothing_from_validation() =>
        Spec.Given(Lisbon)
            .When(RecordExpense("", -5, M9, null, Mallory))
            .ThenNotFound("group not found");

    [Fact]
    public void A_renamed_group_is_folded_under_its_new_name() =>
        Assert.Equal("Porto trip", Fold.Of<State>([.. Lisbon, new GroupRenamed("Porto trip", Bob)])!.GroupName);

    [Fact]
    public void The_state_starts_with_the_original_default_and_follows_the_latest_change()
    {
        Assert.Equal(("equal", 0), (Fold.Of<State>(Lisbon)!.DefaultMode, Fold.Of<State>(Lisbon)!.DefaultShares.Count));

        var state = Fold.Of<State>([.. Lisbon,
            new GroupDefaultSplitChanged("shares", [new MemberShares(M1, 2), new MemberShares(M3, 0)], Alice)])!;

        Assert.Equal("shares", state.DefaultMode);
        Assert.Equal(2, state.DefaultShares[M1]);
        Assert.Equal(0, state.DefaultShares[M3]);
    }
}
