using SplitIt.Shared;
using SplitIt.Slices.EditExpense;
using SplitIt.Tests.Specs;
using ExpenseRecorded = SplitIt.Slices.RecordExpense.ExpenseRecorded;
using ExpenseRemoved = SplitIt.Slices.RemoveExpense.ExpenseRemoved;
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;
using SettlementRecorded = SplitIt.Slices.RecordSettlement.SettlementRecorded;
using GroupRenamed = SplitIt.Slices.RenameGroup.GroupRenamed;
using GroupArchived = SplitIt.Slices.ArchiveGroup.GroupArchived;

namespace SplitIt.Tests.Slices.EditExpense;

/// <summary>
/// docs/event-model/slice-12-edit-expense.md, line for line. Selected scenarios run
/// against a real store, through HTTP, in <see cref="EditExpenseIntegrationTests"/>.
/// </summary>
public class EditExpenseSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly MemberId M4 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b4"));
    private static readonly MemberId M9 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b9"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly UserId Bob = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));
    private static readonly UserId Dave = new(Guid.Parse("00000000-0000-0000-0000-0000000000c4"));
    private static readonly UserId Mallory = new(Guid.Parse("00000000-0000-0000-0000-0000000000c9"));
    private static readonly ExpenseId E1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e1"));
    private static readonly ExpenseId E2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e2"));
    private static readonly ExpenseId E9 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e9"));
    private static readonly SettlementId S1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000f1"));

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Oct1 = new(2026, 10, 1);

    private static readonly object[] Lisbon =
    [
        new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
        new MemberAdded(M1, "Alice", Alice),
        new MemberClaimed(M1, Alice),
        new MemberAdded(M2, "Bob", Alice),
        new MemberClaimed(M2, Bob),
        new MemberAdded(M3, "Carol", Alice),
        new ExpenseRecorded(E1, "Dinner", 9000, M1, new EqualSplit([M1, M2, M3]),
            [new Split(M1, 3000), new Split(M2, 3000), new Split(M3, 3000)], Oct1, Alice),
    ];

    private static readonly DecideSpec<Command> Spec =
        new((history, command) => Decider.Decide(history.Count == 0 ? null : Fold.Of<State>(history, ignoring: typeof(SettlementRecorded)), command));

    // ── notation, as in the .md ───────────────────────────────────────────────────

    /// <summary>The expense as recorded, edited by <paramref name="by"/>: change what differs with <c>with</c>.</summary>
    private static Command EditExpense(UserId by) =>
        new(E1, "Dinner", 9000, M1, Equal(M1, M2, M3), Oct1, Now, by);

    private static EqualSplit Equal(params MemberId[] members) => new(members);

    private static SharesSplit Shares(params (MemberId Member, int Shares)[] shares) =>
        new([.. shares.Select(s => new MemberShares(s.Member, s.Shares))]);

    private static ExactSplit Exact(params (MemberId Member, long Amount)[] amounts) =>
        new([.. amounts.Select(a => new MemberAmount(a.Member, a.Amount))]);

    private static Split[] Splits(params (MemberId Member, long Amount)[] splits) =>
        [.. splits.Select(s => new Split(s.Member, s.Amount))];

    // ── Edits ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void S1_edits_the_amount_and_the_splits_are_recomputed() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { AmountMinor = 12000 })
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 12000, M1, Equal(M1, M2, M3),
                Splits((M1, 4000), (M2, 4000), (M3, 4000)), Oct1, Alice));

    [Fact]
    public void S2_edits_the_description() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Description = "Team dinner" })
            .ThenEdited(new ExpenseEdited(E1, "Team dinner", 9000, M1, Equal(M1, M2, M3),
                Splits((M1, 3000), (M2, 3000), (M3, 3000)), Oct1, Alice));

    [Fact]
    public void S3_edits_who_shares_it() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Split = Equal(M1, M2) })
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 9000, M1, Equal(M1, M2),
                Splits((M1, 4500), (M2, 4500)), Oct1, Alice));

    [Fact]
    public void S4_edits_the_date() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { PaidOn = new DateOnly(2026, 9, 28) })
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 9000, M1, Equal(M1, M2, M3),
                Splits((M1, 3000), (M2, 3000), (M3, 3000)), new DateOnly(2026, 9, 28), Alice));

    [Fact]
    public void S5_a_new_payer_re_splits_the_leftover_goes_to_the_payer_first() =>
        Spec.Given([.. Lisbon,
                new ExpenseRecorded(E2, "Coffee", 1000, M1, Equal(M1, M2, M3), Splits((M1, 334), (M2, 333), (M3, 333)), Oct1, Alice)])
            .When(EditExpense(Alice) with { ExpenseId = E2, Description = "Coffee", AmountMinor = 1000, PayerMemberId = M2 })
            .ThenEdited(new ExpenseEdited(E2, "Coffee", 1000, M2, Equal(M1, M2, M3),
                Splits((M1, 333), (M2, 334), (M3, 333)), Oct1, Alice));

    [Fact]
    public void S6_a_placeholder_can_become_the_payer_and_share() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { PayerMemberId = M3, Split = Equal(M2, M3) })
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 9000, M3, Equal(M2, M3),
                Splits((M2, 4500), (M3, 4500)), Oct1, Alice));

    [Fact]
    public void S7_edits_everything_at_once() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with
            {
                Description = "Taxi", AmountMinor = 3000, PayerMemberId = M3, Split = Equal(M2, M3), PaidOn = new DateOnly(2026, 10, 2),
            })
            .ThenEdited(new ExpenseEdited(E1, "Taxi", 3000, M3, Equal(M2, M3),
                Splits((M2, 1500), (M3, 1500)), new DateOnly(2026, 10, 2), Alice));

    [Fact]
    public void S8_the_split_is_recorded_in_member_added_order() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Split = Equal(M3, M1) })
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 9000, M1, Equal(M1, M3),
                Splits((M1, 4500), (M3, 4500)), Oct1, Alice));

    [Fact]
    public void S9_any_member_may_edit_it_and_is_recorded_as_the_actor() =>
        Spec.Given([.. Lisbon, new MemberAdded(M4, "Dave", Alice), new MemberClaimed(M4, Dave)])
            .When(EditExpense(Dave) with { AmountMinor = 12000 })
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 12000, M1, Equal(M1, M2, M3),
                Splits((M1, 4000), (M2, 4000), (M3, 4000)), Oct1, Dave));

    [Fact]
    public void S10_an_expense_settled_against_can_be_edited() =>
        Spec.Given([.. Lisbon, new SettlementRecorded(S1, M2, M1, 3000, new DateOnly(2026, 10, 2), Bob)])
            .When(EditExpense(Alice) with { AmountMinor = 12000 })
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 12000, M1, Equal(M1, M2, M3),
                Splits((M1, 4000), (M2, 4000), (M3, 4000)), Oct1, Alice));

    [Fact]
    public void S11_last_write_wins_an_expense_already_edited_is_edited_again() =>
        Spec.Given([.. Lisbon, Edited(12000, 4000, Bob)])
            .When(EditExpense(Alice) with { AmountMinor = 15000 })
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 15000, M1, Equal(M1, M2, M3),
                Splits((M1, 5000), (M2, 5000), (M3, 5000)), Oct1, Alice));

    [Fact]
    public void S12_an_edit_is_compared_with_the_latest_edit_not_the_recording() =>
        Spec.Given([.. Lisbon, Edited(12000, 4000, Bob)])
            .When(EditExpense(Alice) with { AmountMinor = 9000 })
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 9000, M1, Equal(M1, M2, M3),
                Splits((M1, 3000), (M2, 3000), (M3, 3000)), Oct1, Alice));

    [Fact]
    public void S13_an_edit_that_changes_nothing_appends_nothing() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice))
            .ThenUnchanged("nothing changed");

    [Fact]
    public void S14_nor_does_one_that_only_reorders_the_split_or_pads_the_description() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Description = "  Dinner ", Split = Equal(M3, M2, M1) })
            .ThenUnchanged("nothing changed");

    [Fact]
    public void S38_an_edit_back_to_what_was_recorded_is_a_change_after_an_edit() =>
        Spec.Given([.. Lisbon, Edited(12000, 4000, Bob)])
            .When(EditExpense(Alice))
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 9000, M1, Equal(M1, M2, M3),
                Splits((M1, 3000), (M2, 3000), (M3, 3000)), Oct1, Alice));

    // ── Who may, and what ─────────────────────────────────────────────────────────

    [Fact]
    public void S15_the_group_must_exist() =>
        Spec.Given()
            .When(EditExpense(Alice) with { AmountMinor = 12000 })
            .ThenNotFound("group not found");

    [Fact]
    public void S16_only_members_may_edit() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Mallory) with { AmountMinor = 12000 })
            .ThenNotFound("group not found");

    [Fact]
    public void S17_the_expense_must_be_in_the_group() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { ExpenseId = E9, AmountMinor = 12000 })
            .ThenNotFound("expense not found");

    [Fact]
    public void S18_a_removed_expense_cannot_be_edited() =>
        Spec.Given([.. Lisbon, new ExpenseRemoved(E1, Bob)])
            .When(EditExpense(Alice) with { AmountMinor = 12000 })
            .ThenNotFound("expense not found");

    [Fact]
    public void S19_a_non_member_gets_group_not_found_whatever_else_is_wrong() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Mallory) with { ExpenseId = E9, Description = "", AmountMinor = 0 })
            .ThenNotFound("group not found");

    // ── Validation: Record expense's rules, here too ─────────────────────────────

    [Fact]
    public void S20_a_description_is_required() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Description = "   " })
            .ThenRejected("description is required");

    [Fact]
    public void S21_a_description_is_at_most_100_characters() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Description = new string('x', 101) })
            .ThenRejected("description must be at most 100 characters");

    [Fact]
    public void S39_a_description_of_100_characters_is_fine() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Description = new string('x', 100) })
            .ThenEdited(new ExpenseEdited(E1, new string('x', 100), 9000, M1, Equal(M1, M2, M3),
                Splits((M1, 3000), (M2, 3000), (M3, 3000)), Oct1, Alice));

    [Fact]
    public void S22_the_amount_must_be_readable() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { AmountMinor = null })
            .ThenRejected("amount must be a number");

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void S23_the_amount_must_be_positive(long amount) =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { AmountMinor = amount })
            .ThenRejected("amount must be positive");

    [Fact]
    public void S24_the_amount_has_a_ceiling() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { AmountMinor = 1_000_000_000_001 })
            .ThenRejected("amount is too large");

    [Fact]
    public void S40_the_ceiling_itself_is_fine() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { AmountMinor = 1_000_000_000_000, Split = Equal(M1) })
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 1_000_000_000_000, M1, Equal(M1),
                Splits((M1, 1_000_000_000_000)), Oct1, Alice));

    [Fact]
    public void S25_the_payer_must_be_a_member_slot_of_the_group() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { PayerMemberId = M9 })
            .ThenRejected("payer is not a member of the group");

    [Fact]
    public void S26_a_payer_is_required() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { PayerMemberId = null })
            .ThenRejected("payer is not a member of the group");

    [Fact]
    public void S27_a_split_is_required() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Split = null })
            .ThenRejected("a split is required");

    [Fact]
    public void S28_every_participant_must_be_a_member_slot_of_the_group_in_any_mode()
    {
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Split = Equal(M1, M9) })
            .ThenRejected("participant is not a member of the group");
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Split = Shares((M1, 1), (M9, 1)) })
            .ThenRejected("participant is not a member of the group");
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Split = Exact((M1, 4500), (M9, 4500)) })
            .ThenRejected("participant is not a member of the group");
    }

    [Fact]
    public void S29_someone_must_share_it() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Split = Equal() })
            .ThenRejected("at least one participant is required");

    [Fact]
    public void S30_nobody_shares_it_twice() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Split = Exact((M1, 5000), (M2, 2000), (M1, 2000)) })
            .ThenRejected("a participant appears more than once");

    [Fact]
    public void S31_every_share_is_a_positive_whole_number() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Split = Shares((M1, 2), (M2, 0)) })
            .ThenRejected("every share must be a positive whole number");

    [Fact]
    public void S32_no_exact_amount_is_negative() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Split = Exact((M1, 10000), (M2, -1000)) })
            .ThenRejected("every exact amount must be zero or more");

    [Fact]
    public void S33_exact_amounts_must_add_up_to_the_total() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Split = Exact((M1, 5000), (M2, 3999)) })
            .ThenRejected("exact amounts must add up to the total");

    [Fact]
    public void S42_an_exact_split_is_its_own_amounts() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Split = Exact((M1, 5000), (M2, 4000)) })
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 9000, M1, Exact((M1, 5000), (M2, 4000)),
                Splits((M1, 5000), (M2, 4000)), Oct1, Alice));

    [Fact]
    public void S41_a_shares_split_rounds_by_largest_remainder() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { AmountMinor = 100, Split = Shares((M1, 1), (M2, 2)) })
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 100, M1, Shares((M1, 1), (M2, 2)),
                Splits((M1, 33), (M2, 67)), Oct1, Alice));

    [Fact]
    public void S34_a_date_is_required() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { PaidOn = null })
            .ThenRejected("date is required");

    [Fact]
    public void S35_the_date_may_be_tomorrow_for_time_zones() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { PaidOn = new DateOnly(2026, 10, 3) })
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 9000, M1, Equal(M1, M2, M3),
                Splits((M1, 3000), (M2, 3000), (M3, 3000)), new DateOnly(2026, 10, 3), Alice));

    [Fact]
    public void S36_but_no_later() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { PaidOn = new DateOnly(2026, 10, 4) })
            .ThenRejected("date cannot be in the future");

    [Fact]
    public void S37_and_any_earlier_date_is_fine_before_the_group_too() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { PaidOn = new DateOnly(2026, 6, 15) })
            .ThenEdited(new ExpenseEdited(E1, "Dinner", 9000, M1, Equal(M1, M2, M3),
                Splits((M1, 3000), (M2, 3000), (M3, 3000)), new DateOnly(2026, 6, 15), Alice));

    [Fact]
    public void S43_an_edit_is_validated_before_it_is_compared() =>
        Spec.Given(Lisbon)
            .When(EditExpense(Alice) with { Split = Equal(M1, M2, M3, M9) })
            .ThenRejected("participant is not a member of the group");

    private static ExpenseEdited Edited(long amount, long each, UserId by) =>
        new(E1, "Dinner", amount, M1, Equal(M1, M2, M3), Splits((M1, each), (M2, each), (M3, each)), Oct1, by);

    [Fact]
    public void A_renamed_group_is_folded_under_its_new_name() =>
        Assert.Equal("Porto trip", Fold.Of<State>([.. Lisbon, new GroupRenamed("Porto trip", Bob)], ignoring: typeof(SettlementRecorded))!.GroupName);

    [Fact]
    public void S44_an_archived_group_edits_no_expense() =>
        Spec.Given([.. Lisbon, new GroupArchived(Alice)])
            .When(EditExpense(Alice) with { AmountMinor = 12000 })
            .ThenRejected("group is archived");
}

internal static class EditAssertions
{
    /// <summary>Compares the amounts by content: the event's list is not a record itself.</summary>
    public static void ThenEdited(this DecideSpec<Command>.WhenStage when, ExpenseEdited expected)
    {
        if (when.Decision is not Decision.Accepted accepted)
            throw Xunit.Sdk.FailException.ForFailure($"Expected {expected}, but got {when.Decision}");
        var actual = Assert.IsType<ExpenseEdited>(Assert.Single(accepted.Events));
        Assert.Equal(expected with { Splits = [] }, actual with { Splits = [] });
        Assert.Equal(expected.Splits, actual.Splits);
    }
}
