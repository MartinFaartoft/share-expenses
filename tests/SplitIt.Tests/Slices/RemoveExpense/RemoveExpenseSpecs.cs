using SplitIt.Shared;
using SplitIt.Slices.RemoveExpense;
using SplitIt.Tests.Specs;
using ExpenseEdited = SplitIt.Slices.EditExpense.ExpenseEdited;
using ExpenseRecorded = SplitIt.Slices.RecordExpense.ExpenseRecorded;
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;
using SettlementRecorded = SplitIt.Slices.RecordSettlement.SettlementRecorded;
using GroupRenamed = SplitIt.Slices.RenameGroup.GroupRenamed;

namespace SplitIt.Tests.Slices.RemoveExpense;

/// <summary>
/// docs/event-model/slice-11-remove-expense.md, line for line. Selected scenarios run
/// against a real store, through HTTP, in <see cref="RemoveExpenseIntegrationTests"/>.
/// </summary>
public class RemoveExpenseSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly MemberId M4 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b4"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly UserId Bob = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));
    private static readonly UserId Dave = new(Guid.Parse("00000000-0000-0000-0000-0000000000c4"));
    private static readonly UserId Mallory = new(Guid.Parse("00000000-0000-0000-0000-0000000000c9"));
    private static readonly ExpenseId E1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e1"));
    private static readonly ExpenseId E2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e2"));
    private static readonly ExpenseId E9 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e9"));
    private static readonly SettlementId S1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000f1"));
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

    [Fact]
    public void S1_removes_an_expense() =>
        Spec.Given(Lisbon)
            .When(new Command(E1, Alice))
            .Then(new ExpenseRemoved(E1, Alice));

    [Fact]
    public void S2_any_member_may_remove_it_in_its_split_or_not() =>
        Spec.Given([.. Lisbon, new MemberAdded(M4, "Dave", Alice), new MemberClaimed(M4, Dave)])
            .When(new Command(E1, Dave))
            .Then(new ExpenseRemoved(E1, Dave));

    [Fact]
    public void S3_an_expense_paid_by_a_placeholder_can_be_removed_too() =>
        Spec.Given([.. Lisbon,
                new ExpenseRecorded(E2, "Taxi", 3000, M3, new EqualSplit([M1, M3]), [new Split(M1, 1500), new Split(M3, 1500)], Oct1, Alice)])
            .When(new Command(E2, Bob))
            .Then(new ExpenseRemoved(E2, Bob));

    [Fact]
    public void S4_an_expense_settled_against_can_be_removed() =>
        Spec.Given([.. Lisbon, new SettlementRecorded(S1, M2, M1, 3000, new DateOnly(2026, 10, 2), Bob)])
            .When(new Command(E1, Alice))
            .Then(new ExpenseRemoved(E1, Alice));

    [Fact]
    public void An_edited_expense_can_be_removed() =>
        Spec.Given([.. Lisbon,
                new ExpenseEdited(E1, "Team dinner", 12000, M1, new EqualSplit([M1, M2, M3]),
                    [new Split(M1, 4000), new Split(M2, 4000), new Split(M3, 4000)], Oct1, Bob)])
            .When(new Command(E1, Alice))
            .Then(new ExpenseRemoved(E1, Alice));

    [Fact]
    public void The_confirm_page_knows_an_expense_as_last_edited() =>
        Assert.Equal(
            new RecordedExpense("Team dinner", 12000, M2, new DateOnly(2026, 9, 28)),
            Fold.Of<State>([.. Lisbon,
                new ExpenseEdited(E1, "Team dinner", 12000, M2, new EqualSplit([M1, M2, M3]),
                    [new Split(M1, 4000), new Split(M2, 4000), new Split(M3, 4000)], new DateOnly(2026, 9, 28), Bob)],
                ignoring: typeof(SettlementRecorded))!.Expenses[E1]);

    [Fact]
    public void S5_the_group_must_exist() =>
        Spec.Given()
            .When(new Command(E1, Alice))
            .ThenNotFound("group not found");

    [Fact]
    public void S6_only_members_may_remove() =>
        Spec.Given(Lisbon)
            .When(new Command(E1, Mallory))
            .ThenNotFound("group not found");

    [Fact]
    public void S7_the_expense_must_be_in_the_group() =>
        Spec.Given(Lisbon)
            .When(new Command(E9, Alice))
            .ThenNotFound("expense not found");

    [Fact]
    public void S8_an_expense_already_removed_is_not_removed_twice() =>
        Spec.Given([.. Lisbon, new ExpenseRemoved(E1, Bob)])
            .When(new Command(E1, Alice))
            .ThenAlreadyRemoved("expense already removed");

    [Fact]
    public void A_renamed_group_is_folded_under_its_new_name() =>
        Assert.Equal("Porto trip", Fold.Of<State>([.. Lisbon, new GroupRenamed("Porto trip", Bob)], ignoring: typeof(SettlementRecorded))!.GroupName);
}
