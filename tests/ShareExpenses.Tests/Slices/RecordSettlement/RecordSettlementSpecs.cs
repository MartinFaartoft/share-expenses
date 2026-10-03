using ShareExpenses.Shared;
using ShareExpenses.Slices.RecordSettlement;
using ShareExpenses.Tests.Specs;
// Only the public events: tests can see every slice's internals, so a namespace
// import would bring in CreateGroup's own Command too.
using ExpenseRecorded = ShareExpenses.Slices.RecordExpense.ExpenseRecorded;
using GroupCreated = ShareExpenses.Slices.CreateGroup.GroupCreated;
using MemberAdded = ShareExpenses.Slices.CreateGroup.MemberAdded;
using MemberClaimed = ShareExpenses.Slices.CreateGroup.MemberClaimed;

namespace ShareExpenses.Tests.Slices.RecordSettlement;

/// <summary>
/// docs/event-model/slice-08-record-settlement.md, line for line. Selected scenarios
/// run against a real store, through HTTP, in <see cref="RecordSettlementIntegrationTests"/>.
/// </summary>
public class RecordSettlementSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly MemberId M9 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b9"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly UserId Bob = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));
    private static readonly UserId Mallory = new(Guid.Parse("00000000-0000-0000-0000-0000000000c9"));
    private static readonly SettlementId S1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000f1"));
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

    // ExpenseRecorded is ignored on purpose: deciding folds no balances (spec §7).
    private static readonly DecideSpec<Command> Spec =
        new((history, command) => Decider.Decide(Fold.Of<State>(history, ignoring: typeof(ExpenseRecorded)), command));

    private static Command RecordSettlement(MemberId? from, MemberId? to, long amount, UserId by, DateOnly? paidOn = null) =>
        new(S1, from, to, amount, paidOn ?? Oct1, Now, by);

    [Fact]
    public void S1_records_a_settlement() =>
        Spec.Given(Lisbon)
            .When(RecordSettlement(M2, M1, 3000, Bob))
            .Then(new SettlementRecorded(S1, M2, M1, 3000, Oct1, Bob));

    [Fact]
    public void S2_any_member_may_record_it_party_to_it_or_not() =>
        Spec.Given(Lisbon)
            .When(RecordSettlement(M2, M1, 3000, Alice))
            .Then(new SettlementRecorded(S1, M2, M1, 3000, Oct1, Alice));

    [Fact]
    public void S3_a_placeholder_can_pay_and_be_paid() =>
        Spec.Given(Lisbon)
            .When(RecordSettlement(M3, M1, 3000, Bob))
            .Then(new SettlementRecorded(S1, M3, M1, 3000, Oct1, Bob));

    [Fact]
    public void S4_nothing_need_be_owed_balances_absorb_any_payment() =>
        Spec.Given(
            [
                .. Lisbon,
                new ExpenseRecorded(E1, "Dinner", 9000, M1, new EqualSplit([M1, M2, M3]),
                    [new Split(M1, 3000), new Split(M2, 3000), new Split(M3, 3000)], Oct1, Alice),
            ])
            .When(RecordSettlement(M1, M2, 5000, Alice))
            .Then(new SettlementRecorded(S1, M1, M2, 5000, Oct1, Alice));

    [Fact]
    public void S5_the_group_must_exist() =>
        Spec.Given()
            .When(RecordSettlement(M2, M1, 3000, Bob))
            .ThenNotFound("group not found");

    [Fact]
    public void S6_only_members_of_the_group_may_record() =>
        Spec.Given(Lisbon)
            .When(RecordSettlement(M2, M1, 3000, Mallory))
            .ThenNotFound("group not found");

    [Fact]
    public void S7_the_payer_must_be_a_member_slot_of_the_group() =>
        Spec.Given(Lisbon)
            .When(RecordSettlement(M9, M1, 3000, Bob))
            .ThenRejected("payer is not a member of the group");

    [Fact]
    public void S7_a_missing_payer_is_not_a_member() =>
        Spec.Given(Lisbon)
            .When(RecordSettlement(null, M1, 3000, Bob))
            .ThenRejected("payer is not a member of the group");

    [Fact]
    public void S8_so_must_the_recipient() =>
        Spec.Given(Lisbon)
            .When(RecordSettlement(M2, M9, 3000, Bob))
            .ThenRejected("recipient is not a member of the group");

    [Fact]
    public void S9_nobody_pays_themselves() =>
        Spec.Given(Lisbon)
            .When(RecordSettlement(M2, M2, 3000, Bob))
            .ThenRejected("a member cannot pay themselves");

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void S10_the_amount_must_be_positive(long amount) =>
        Spec.Given(Lisbon)
            .When(RecordSettlement(M2, M1, amount, Bob))
            .ThenRejected("amount must be positive");

    [Fact]
    public void S11_the_amount_has_a_ceiling() =>
        Spec.Given(Lisbon)
            .When(RecordSettlement(M2, M1, 1_000_000_000_001, Bob))
            .ThenRejected("amount is too large");

    [Fact]
    public void S12_the_date_may_be_tomorrow_for_time_zones() =>
        Spec.Given(Lisbon)
            .When(RecordSettlement(M2, M1, 3000, Bob, paidOn: new DateOnly(2026, 10, 3)))
            .Then(new SettlementRecorded(S1, M2, M1, 3000, new DateOnly(2026, 10, 3), Bob));

    [Fact]
    public void S13_but_no_later() =>
        Spec.Given(Lisbon)
            .When(RecordSettlement(M2, M1, 3000, Bob, paidOn: new DateOnly(2026, 10, 4)))
            .ThenRejected("date cannot be in the future");

    [Fact]
    public void S14_a_date_is_required() =>
        Spec.Given(Lisbon)
            .When(new Command(S1, M2, M1, 3000, null, Now, Bob))
            .ThenRejected("date is required");

    [Fact]
    public void A_non_member_learns_nothing_from_validation() =>
        Spec.Given(Lisbon)
            .When(RecordSettlement(M9, M9, -5, Mallory))
            .ThenNotFound("group not found");
}
