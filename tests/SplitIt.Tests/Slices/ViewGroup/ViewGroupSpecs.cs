using SplitIt.Shared;
using SplitIt.Slices.ViewGroup;
using SplitIt.Tests.Specs;
// Only the public events: tests can see every slice's internals, so namespace
// imports would bring in other slices' Command, State and Endpoint too.
using ExpenseRecorded = SplitIt.Slices.RecordExpense.ExpenseRecorded;
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;
using ExpenseEdited = SplitIt.Slices.EditExpense.ExpenseEdited;
using ExpenseRemoved = SplitIt.Slices.RemoveExpense.ExpenseRemoved;
using SettlementRecorded = SplitIt.Slices.RecordSettlement.SettlementRecorded;

namespace SplitIt.Tests.Slices.ViewGroup;

/// <summary>
/// docs/event-model/slice-07-view-group.md, line for line, against the fold and the
/// pure read. The same fold runs as Marten's inline projection, through HTTP, in
/// <see cref="ViewGroupIntegrationTests"/>.
/// </summary>
public class ViewGroupSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly UserId Bob = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));
    private static readonly UserId Carol = new(Guid.Parse("00000000-0000-0000-0000-0000000000c3"));
    private static readonly UserId Mallory = new(Guid.Parse("00000000-0000-0000-0000-0000000000c9"));
    private static readonly ExpenseId E1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e1"));
    private static readonly ExpenseId E2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e2"));
    private static readonly ExpenseId E3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000e3"));
    private static readonly SettlementId S1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000f1"));

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

    private static readonly Query AsAlice = new(Alice);

    private static GroupActivityReadModel? Read(IReadOnlyList<object> history, Query query) =>
        Reader.Read(history.Count == 0 ? null : Fold.Of<State>(history), query);

    private static GroupActivityReadModel View(IReadOnlyList<object> history, Query? query = null) =>
        Read(history, query ?? AsAlice) ?? throw new Xunit.Sdk.XunitException("Expected the group, but found nothing");

    private static readonly (MemberId, long)[] Dinner = [(M1, 3000), (M2, 3000), (M3, 3000)];

    private static ExpenseRecorded Expense(
        ExpenseId id, string description, long amount, MemberId payer, (MemberId Member, long Amount)[] splits,
        DateOnly? paidOn = null) =>
        new(id, description, amount, payer, new EqualSplit([.. splits.Select(s => s.Member)]),
            [.. splits.Select(s => new Split(s.Member, s.Amount))], paidOn ?? Oct1, Alice);

    [Fact]
    public void S1_a_new_group_nothing_yet_and_you_are_settled_up()
    {
        var group = View(Lisbon);

        Assert.Equal(("Lisbon trip", "GBP", M1, 0L), (group.GroupName, group.Currency, group.You, group.BalanceMinor));
        Assert.Empty(group.History);
    }

    [Fact]
    public void S2_an_expense_joins_the_history_with_who_paid()
    {
        var group = View([.. Lisbon, Expense(E1, "Dinner", 9000, M1, Dinner)]);

        var expense = Assert.IsType<ActivityExpense>(Assert.Single(group.History));
        Assert.Equal((E1, "Dinner", 9000L, M1, "Alice", Oct1),
            (expense.ExpenseId, expense.Description, expense.AmountMinor, expense.PayerMemberId, expense.PayerName, expense.PaidOn));
        Assert.Equal(new EqualSplit([M1, M2, M3]), expense.Split);
        Assert.Equal([new Split(M1, 3000), new Split(M2, 3000), new Split(M3, 3000)], expense.Splits);
    }

    [Fact]
    public void S3_your_standing_owed_after_paying_for_others() =>
        Assert.Equal(6000, View([.. Lisbon, Expense(E1, "Dinner", 9000, M1, Dinner)]).BalanceMinor);

    [Fact]
    public void S4_and_owing_after_others_paid_for_you()
    {
        var group = View([.. Lisbon, Expense(E1, "Dinner", 9000, M1, Dinner)], new Query(Bob));

        Assert.Equal((M2, -3000L), (group.You, group.BalanceMinor));
    }

    [Fact]
    public void S5_a_settlement_joins_the_history_and_moves_your_standing()
    {
        var group = View(
            [
                .. Lisbon,
                Expense(E1, "Dinner", 9000, M1, Dinner),
                new SettlementRecorded(S1, M2, M1, 3000, new DateOnly(2026, 10, 2), Bob),
            ],
            new Query(Bob));

        Assert.Equal(0, group.BalanceMinor);
        Assert.Equal(new ActivitySettlement(S1, M2, "Bob", M1, "Alice", 3000, new DateOnly(2026, 10, 2)), group.History[0]);
        Assert.IsType<ActivityExpense>(group.History[1]);
    }

    [Fact]
    public void S6_history_newest_first_by_date_then_most_recently_recorded() =>
        Assert.Equal(
            ["Coffee", "settlement", "Dinner", "Flights"],
            View(
            [
                .. Lisbon,
                Expense(E1, "Flights", 60000, M1, [(M1, 60000)], new DateOnly(2026, 6, 15)),
                Expense(E2, "Dinner", 9000, M1, [(M1, 9000)], Oct1),
                new SettlementRecorded(S1, M2, M1, 3000, Oct1, Bob),
                Expense(E3, "Coffee", 300, M1, [(M1, 300)], Oct1),
            ]).History.Select(h => h is ActivityExpense e ? e.Description : "settlement"));

    [Fact]
    public void S7_the_group_must_exist() =>
        Assert.Null(Read([], AsAlice));

    [Fact]
    public void S8_only_members_may_view() =>
        Assert.Null(Read(Lisbon, new Query(Mallory)));

    [Fact]
    public void S9_an_unclaimed_placeholder_confers_no_access() =>
        // Carol's slot exists, but no user holds it: Carol herself is not a member yet.
        Assert.Null(Read(Lisbon, new Query(Carol)));

    [Fact]
    public void A_removed_expense_leaves_the_history_and_your_standing_moves_back()
    {
        var group = View([.. Lisbon, Expense(E1, "Dinner", 9000, M1, Dinner), new ExpenseRemoved(E1, Bob)]);

        Assert.Empty(group.History);
        Assert.Equal(0, group.BalanceMinor);
    }

    [Fact]
    public void Removing_one_expense_leaves_the_others_counting()
    {
        var group = View(
        [
            .. Lisbon,
            Expense(E1, "Dinner", 9000, M1, Dinner),
            Expense(E2, "Taxi", 3000, M2, [(M2, 1500), (M3, 1500)]),
            new ExpenseRemoved(E1, Alice),
        ]);

        Assert.Equal(["Taxi"], group.History.Select(h => ((ActivityExpense)h).Description));
        Assert.Equal(0, group.BalanceMinor);
    }

    [Fact]
    public void Entries_recorded_after_a_removal_keep_their_order()
    {
        var group = View(
        [
            .. Lisbon,
            Expense(E1, "Dinner", 9000, M1, Dinner),
            Expense(E2, "Taxi", 3000, M1, [(M1, 3000)]),
            new ExpenseRemoved(E1, Alice),
            Expense(E3, "Coffee", 300, M1, [(M1, 300)]),
            new SettlementRecorded(S1, M2, M1, 100, Oct1, Bob),
        ]);

        Assert.Equal(
            ["settlement", "Coffee", "Taxi"],
            group.History.Select(h => h is ActivityExpense e ? e.Description : "settlement"));
    }

    [Fact]
    public void Removing_an_unknown_expense_changes_nothing() =>
        Assert.Equal(
            View([.. Lisbon, Expense(E1, "Dinner", 9000, M1, Dinner)]),
            View([.. Lisbon, Expense(E1, "Dinner", 9000, M1, Dinner), new ExpenseRemoved(E2, Alice)]),
            new ReadModelComparer());

    private sealed class ReadModelComparer : IEqualityComparer<GroupActivityReadModel>
    {
        public bool Equals(GroupActivityReadModel? x, GroupActivityReadModel? y) =>
            x!.BalanceMinor == y!.BalanceMinor && x.History.Count == y.History.Count;

        public int GetHashCode(GroupActivityReadModel obj) => 0;
    }

    private static ExpenseEdited Edit(ExpenseId id, string description, long amount, MemberId payer, (MemberId Member, long Amount)[] splits, DateOnly? paidOn = null) =>
        new(id, description, amount, payer, new EqualSplit([.. splits.Select(s => s.Member)]),
            [.. splits.Select(s => new Split(s.Member, s.Amount))], paidOn ?? Oct1, Bob);

    [Fact]
    public void An_edited_expense_shows_its_new_values_and_your_standing_moves_to_them()
    {
        var group = View([.. Lisbon, Expense(E1, "Dinner", 9000, M1, Dinner),
            Edit(E1, "Team dinner", 12000, M1, [(M1, 4000), (M2, 4000), (M3, 4000)], new DateOnly(2026, 9, 28))]);

        var edited = Assert.IsType<ActivityExpense>(Assert.Single(group.History));
        Assert.Equal((E1, "Team dinner", 12000L, new DateOnly(2026, 9, 28)), (edited.ExpenseId, edited.Description, edited.AmountMinor, edited.PaidOn));
        Assert.Equal([new Split(M1, 4000), new Split(M2, 4000), new Split(M3, 4000)], edited.Splits);
        Assert.Equal(8000, group.BalanceMinor);
    }

    [Fact]
    public void An_edit_can_change_the_payer_and_who_shares_it()
    {
        var group = View([.. Lisbon, Expense(E1, "Dinner", 9000, M1, Dinner), Edit(E1, "Dinner", 6000, M2, [(M1, 3000), (M2, 3000)])]);

        Assert.Equal(-3000, group.BalanceMinor);
    }

    [Fact]
    public void An_edited_expense_keeps_its_place_in_the_history()
    {
        var group = View(
        [
            .. Lisbon,
            Expense(E1, "Dinner", 9000, M1, Dinner),
            Expense(E2, "Taxi", 3000, M1, [(M1, 3000)]),
            Edit(E1, "Team dinner", 9000, M1, Dinner),
        ]);

        Assert.Equal(["Taxi", "Team dinner"], group.History.Select(h => ((ActivityExpense)h).Description));
    }

    [Fact]
    public void An_edited_expense_moves_to_the_day_it_is_now_paid_on()
    {
        var group = View(
        [
            .. Lisbon,
            Expense(E1, "Dinner", 9000, M1, Dinner),
            Expense(E2, "Taxi", 3000, M1, [(M1, 3000)]),
            Edit(E1, "Dinner", 9000, M1, Dinner, new DateOnly(2026, 10, 2)),
        ]);

        Assert.Equal(["Dinner", "Taxi"], group.History.Select(h => ((ActivityExpense)h).Description));
    }

    [Fact]
    public void An_edited_expense_can_still_be_removed_and_leaves_nothing_behind()
    {
        var group = View([.. Lisbon, Expense(E1, "Dinner", 9000, M1, Dinner),
            Edit(E1, "Team dinner", 12000, M1, [(M1, 4000), (M2, 4000), (M3, 4000)]), new ExpenseRemoved(E1, Alice)]);

        Assert.Empty(group.History);
        Assert.Equal(0, group.BalanceMinor);
    }

    [Fact]
    public void Editing_an_unknown_expense_changes_nothing() =>
        Assert.Equal(
            View([.. Lisbon, Expense(E1, "Dinner", 9000, M1, Dinner)]),
            View([.. Lisbon, Expense(E1, "Dinner", 9000, M1, Dinner), Edit(E2, "Taxi", 3000, M1, [(M1, 3000)])]),
            new ReadModelComparer());
}
