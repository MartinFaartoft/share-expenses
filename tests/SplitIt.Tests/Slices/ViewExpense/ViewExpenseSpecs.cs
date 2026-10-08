using SplitIt.Shared;
using SplitIt.Slices.ViewExpense;
using SplitIt.Tests.Specs;
// Only the public events: tests can see every slice's internals, so namespace
// imports would bring in other slices' Command, State and Endpoint too.
using ExpenseEdited = SplitIt.Slices.EditExpense.ExpenseEdited;
using ExpenseRecorded = SplitIt.Slices.RecordExpense.ExpenseRecorded;
using ExpenseRemoved = SplitIt.Slices.RemoveExpense.ExpenseRemoved;
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;
using SettlementRecorded = SplitIt.Slices.RecordSettlement.SettlementRecorded;
using GroupArchived = SplitIt.Slices.ArchiveGroup.GroupArchived;

namespace SplitIt.Tests.Slices.ViewExpense;

/// <summary>
/// docs/event-model/slice-13-view-expense.md, line for line, against the fold and the
/// pure read. The same fold runs, through HTTP, in <see cref="ViewExpenseIntegrationTests"/>.
/// </summary>
public class ViewExpenseSpecs
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

    private static ExpenseReadModel? Read(IReadOnlyList<object> history, ExpenseId expense, UserId user) =>
        Reader.Read(history.Count == 0 ? null : Fold.Of<State>(history, ignoring: typeof(SettlementRecorded)), new Query(expense, user));

    private static ExpenseReadModel View(IReadOnlyList<object> history, ExpenseId expense, UserId user) =>
        Read(history, expense, user) ?? throw new Xunit.Sdk.XunitException("Expected the expense, but found nothing");

    /// <summary>The lines as the .md writes them: "Alice 3000", or "Alice ×2 500" for shares.</summary>
    private static string[] Lines(ExpenseReadModel model) =>
        [.. model.Shares.Select(s => $"{s.Name}{(s.Weight is { } w ? $" ×{w}" : "")} {s.AmountMinor}")];

    private static ExpenseRecorded Recorded(
        ExpenseId id, string description, long amount, MemberId payer, ExpenseSplit split, params (MemberId Member, long Amount)[] splits) =>
        new(id, description, amount, payer, split, [.. splits.Select(s => new Split(s.Member, s.Amount))], Oct1, Alice);

    [Fact]
    public void S1_shows_the_expense_and_who_shares_it()
    {
        var expense = View(Lisbon, E1, Alice);

        Assert.Equal((E1, "GBP", M1), (expense.ExpenseId, expense.Currency, expense.You));
        Assert.Equal(("Dinner", 9000L, M1, "Alice", Oct1, "equal"),
            (expense.Description, expense.AmountMinor, expense.PayerMemberId, expense.PayerName, expense.PaidOn, expense.Mode));
        Assert.Equal(["Alice 3000", "Bob 3000", "Carol 3000"], Lines(expense));
    }

    [Fact]
    public void S2_the_payer_need_not_share() =>
        Assert.Equal(["Bob 4500"], Lines(View(
            [.. Lisbon, Recorded(E2, "Bob's ticket", 4500, M1, new EqualSplit([M2]), (M2, 4500))], E2, Alice)));

    [Fact]
    public void S3_a_placeholder_can_have_paid_it()
    {
        var expense = View(
            [.. Lisbon, Recorded(E2, "Taxi", 3000, M3, new EqualSplit([M1, M3]), (M1, 1500), (M3, 1500))], E2, Alice);

        Assert.Equal((M3, "Carol"), (expense.PayerMemberId, expense.PayerName));
        Assert.Equal(["Alice 1500", "Carol 1500"], Lines(expense));
    }

    [Fact]
    public void S4_a_shares_split_shows_each_weight()
    {
        var expense = View(
        [
            .. Lisbon,
            Recorded(E2, "Flat", 1000, M1,
                new SharesSplit([new MemberShares(M1, 2), new MemberShares(M2, 1), new MemberShares(M3, 1)]),
                (M1, 500), (M2, 250), (M3, 250)),
        ], E2, Alice);

        Assert.Equal("shares", expense.Mode);
        Assert.Equal(["Alice ×2 500", "Bob ×1 250", "Carol ×1 250"], Lines(expense));
    }

    [Fact]
    public void S5_an_exact_split_shows_each_amount()
    {
        var expense = View(
        [
            .. Lisbon,
            Recorded(E2, "Steak night", 5000, M1, new ExactSplit([new MemberAmount(M1, 2000), new MemberAmount(M2, 3000)]),
                (M1, 2000), (M2, 3000)),
        ], E2, Alice);

        Assert.Equal("exact", expense.Mode);
        Assert.Equal(["Alice 2000", "Bob 3000"], Lines(expense));
    }

    [Fact]
    public void S6_an_edited_expense_shows_its_latest_values()
    {
        var expense = View(
        [
            .. Lisbon,
            new ExpenseEdited(E1, "Team dinner", 6000, M2, new EqualSplit([M1, M2]),
                [new Split(M1, 3000), new Split(M2, 3000)], new DateOnly(2026, 9, 28), Bob),
        ], E1, Alice);

        Assert.Equal(("Team dinner", 6000L, "Bob", new DateOnly(2026, 9, 28)),
            (expense.Description, expense.AmountMinor, expense.PayerName, expense.PaidOn));
        Assert.Equal(["Alice 3000", "Bob 3000"], Lines(expense));
    }

    [Fact]
    public void S7_you_is_the_callers_own_slot() =>
        Assert.Equal(M2, View(Lisbon, E1, Bob).You);

    [Fact]
    public void S8_the_group_must_exist() =>
        Assert.Null(Read([], E1, Alice));

    [Fact]
    public void S9_only_members_may_view() =>
        Assert.Null(Read(Lisbon, E1, Mallory));

    [Fact]
    public void S10_the_expense_must_be_in_the_group() =>
        Assert.Null(Read(Lisbon, E9, Alice));

    [Fact]
    public void S11_a_removed_expense_is_not_found() =>
        Assert.Null(Read([.. Lisbon, new ExpenseRemoved(E1, Bob)], E1, Alice));

    [Fact]
    public void An_edit_of_a_removed_expense_does_not_bring_it_back() =>
        Assert.Null(Read(
        [
            .. Lisbon,
            new ExpenseRemoved(E1, Bob),
            new ExpenseEdited(E1, "Dinner", 9000, M1, new EqualSplit([M1]), [new Split(M1, 9000)], Oct1, Bob),
        ], E1, Alice));

    [Fact]
    public void A_settlement_changes_nothing_on_an_expense() =>
        Assert.Equal(["Alice 3000", "Bob 3000", "Carol 3000"],
            Lines(View([.. Lisbon, new SettlementRecorded(S1, M2, M1, 3000, Oct1, Bob)], E1, Alice)));

    [Fact]
    public void S12_an_archived_groups_expense_is_shown_marked_archived()
    {
        var expense = View([.. Lisbon, new GroupArchived(Bob)], E1, Alice);

        Assert.True(expense.Archived);
        Assert.Equal("Dinner", expense.Description);
    }

    [Fact]
    public void An_expense_is_not_archived_until_its_group_is() =>
        Assert.False(View(Lisbon, E1, Alice).Archived);
}
