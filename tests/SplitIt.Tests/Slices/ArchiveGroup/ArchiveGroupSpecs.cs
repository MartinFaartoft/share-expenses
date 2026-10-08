using SplitIt.Shared;
using SplitIt.Slices.ArchiveGroup;
using SplitIt.Tests.Specs;
// Only the public events: tests can see every slice's internals, so a namespace
// import would bring in CreateGroup's own Command too.
using ExpenseEdited = SplitIt.Slices.EditExpense.ExpenseEdited;
using ExpenseRecorded = SplitIt.Slices.RecordExpense.ExpenseRecorded;
using ExpenseRemoved = SplitIt.Slices.RemoveExpense.ExpenseRemoved;
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;
using SettlementRecorded = SplitIt.Slices.RecordSettlement.SettlementRecorded;

namespace SplitIt.Tests.Slices.ArchiveGroup;

/// <summary>
/// docs/event-model/slice-15-archive-group.md, line for line: deciding, and the balances the
/// confirm page warns with. Selected scenarios run against a real store, through HTTP, in
/// <see cref="ArchiveGroupIntegrationTests"/>.
/// </summary>
public class ArchiveGroupSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly UserId Bob = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));
    private static readonly UserId Mallory = new(Guid.Parse("00000000-0000-0000-0000-0000000000c9"));
    private static readonly ExpenseId E1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e1"));
    private static readonly ExpenseId E2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e2"));
    private static readonly SettlementId S1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000f1"));
    private static readonly SettlementId S2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000f2"));
    private static readonly DateOnly Oct1 = new(2026, 10, 1);

    private static readonly object[] Lisbon =
    [
        new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
        new MemberAdded(M1, "Alice", Alice),
        new MemberClaimed(M1, Alice),
        new MemberAdded(M2, "Bob", Alice),
        new MemberClaimed(M2, Bob),
        new MemberAdded(M3, "Carol", Alice),
    ];

    private static readonly ExpenseRecorded Dinner = new(E1, "Dinner", 9000, M1, new EqualSplit([M1, M2, M3]),
        [new Split(M1, 3000), new Split(M2, 3000), new Split(M3, 3000)], Oct1, Alice);

    private static readonly DecideSpec<Command> Spec =
        new((history, command) => Decider.Decide(history.Count == 0 ? null : Fold.Of<State>(history), command));

    /// <summary>The balances as the confirm page warns with them: "Alice +6000", in member-added order.</summary>
    private static string[] Owing(params object[] history) =>
        [.. Fold.Of<State>(history)!.Slots.Where(s => s.BalanceMinor != 0).Select(s => $"{s.Name} {s.BalanceMinor:+0;-0}")];

    // ── Deciding ──────────────────────────────────────────────────────────────────

    [Fact]
    public void S1_archives_the_group() =>
        Spec.Given(Lisbon)
            .When(new Command(Alice))
            .Then(new GroupArchived(Alice));

    [Fact]
    public void S2_any_member_may_archive_it_and_is_recorded_as_the_actor() =>
        Spec.Given(Lisbon)
            .When(new Command(Bob))
            .Then(new GroupArchived(Bob));

    [Fact]
    public void S3_a_group_that_is_not_settled_up_can_be_archived_the_page_warns_the_rule_does_not() =>
        Spec.Given([.. Lisbon, Dinner])
            .When(new Command(Alice))
            .Then(new GroupArchived(Alice));

    [Fact]
    public void S4_the_group_must_exist() =>
        Spec.Given()
            .When(new Command(Alice))
            .ThenNotFound("group not found");

    [Fact]
    public void S5_only_members_of_the_group_may_archive_it() =>
        Spec.Given(Lisbon)
            .When(new Command(Mallory))
            .ThenNotFound("group not found");

    [Fact]
    public void S6_a_group_already_archived_is_not_archived_twice() =>
        Spec.Given([.. Lisbon, new GroupArchived(Bob)])
            .When(new Command(Alice))
            .ThenUnchanged("group already archived");

    [Fact]
    public void A_non_member_is_told_nothing_even_of_an_archived_group() =>
        Spec.Given([.. Lisbon, new GroupArchived(Bob)])
            .When(new Command(Mallory))
            .ThenNotFound("group not found");

    [Fact]
    public void An_unclaimed_placeholder_has_no_right_to_archive() =>
        Spec.Given(Lisbon)
            .When(new Command(new UserId(Guid.Parse("00000000-0000-0000-0000-0000000000c3"))))
            .ThenNotFound("group not found");

    // ── What the page knows ───────────────────────────────────────────────────────

    [Fact]
    public void A_new_group_is_settled_up() =>
        Assert.Empty(Owing(Lisbon));

    [Fact]
    public void S1s_settled_up_no_warning() =>
        Assert.Empty(Owing([.. Lisbon, Dinner,
            new SettlementRecorded(S1, M2, M1, 3000, Oct1, Bob), new SettlementRecorded(S2, M3, M1, 3000, Oct1, Alice)]));

    [Fact]
    public void S2s_not_settled_up_who_owes_and_who_is_owed_in_member_added_order() =>
        Assert.Equal(["Alice +6000", "Bob -3000", "Carol -3000"], Owing([.. Lisbon, Dinner]));

    [Fact]
    public void A_part_payment_leaves_the_rest()
    {
        Assert.Equal(["Alice +1000", "Bob -1000"], Owing([.. Lisbon, Dinner,
            new SettlementRecorded(S1, M2, M1, 2000, Oct1, Bob), new SettlementRecorded(S2, M3, M1, 3000, Oct1, Alice)]));
    }

    [Fact]
    public void S3s_an_edited_expense_counts_as_it_stands() =>
        Assert.Equal(["Alice +4000", "Bob -2000", "Carol -2000"], Owing([.. Lisbon, Dinner,
            new ExpenseEdited(E1, "Dinner", 6000, M1, new EqualSplit([M1, M2, M3]),
                [new Split(M1, 2000), new Split(M2, 2000), new Split(M3, 2000)], Oct1, Bob)]));

    [Fact]
    public void An_edit_that_changes_the_payer_and_who_shares_moves_the_balances_with_it() =>
        Assert.Equal(["Alice -3000", "Bob +3000"], Owing([.. Lisbon, Dinner,
            new ExpenseEdited(E1, "Dinner", 6000, M2, new EqualSplit([M1, M2]),
                [new Split(M1, 3000), new Split(M2, 3000)], Oct1, Bob)]));

    [Fact]
    public void S3s_a_removed_expense_is_as_if_never_recorded() =>
        Assert.Empty(Owing([.. Lisbon, Dinner, new ExpenseRemoved(E1, Bob)]));

    [Fact]
    public void An_edit_of_a_removed_expense_does_not_bring_it_back() =>
        Assert.Empty(Owing([.. Lisbon, Dinner, new ExpenseRemoved(E1, Bob),
            new ExpenseEdited(E1, "Dinner", 6000, M1, new EqualSplit([M1, M2]), [new Split(M1, 3000), new Split(M2, 3000)], Oct1, Bob)]));

    [Fact]
    public void Balances_always_sum_to_zero()
    {
        var state = Fold.Of<State>([.. Lisbon, Dinner,
            new ExpenseRecorded(E2, "Taxi", 1001, M2, new EqualSplit([M1, M2, M3]),
                [new Split(M1, 334), new Split(M2, 334), new Split(M3, 333)], Oct1, Bob),
            new SettlementRecorded(S1, M3, M1, 1234, Oct1, Alice)])!;

        Assert.Equal(0, state.Slots.Sum(s => s.BalanceMinor));
    }

    [Fact]
    public void The_state_knows_it_is_archived_and_not_before()
    {
        Assert.False(Fold.Of<State>(Lisbon)!.Archived);
        Assert.True(Fold.Of<State>([.. Lisbon, new GroupArchived(Alice)])!.Archived);
    }
}
