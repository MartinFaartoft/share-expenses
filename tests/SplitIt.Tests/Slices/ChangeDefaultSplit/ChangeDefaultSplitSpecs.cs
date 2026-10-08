using SplitIt.Shared;
using SplitIt.Slices.ChangeDefaultSplit;
using SplitIt.Tests.Specs;
// Only the public events: tests can see every slice's internals, so a namespace
// import would bring in CreateGroup's own Command too.
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;

namespace SplitIt.Tests.Slices.ChangeDefaultSplit;

/// <summary>
/// docs/event-model/slice-16-change-default-split.md, line for line (scenario 13, the archived
/// group, is built with Archive group). Selected scenarios run against a real store, through
/// HTTP, in <see cref="ChangeDefaultSplitIntegrationTests"/>.
/// </summary>
public class ChangeDefaultSplitSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly MemberId M4 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b4"));
    private static readonly MemberId M9 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b9"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly UserId Bob = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));
    private static readonly UserId Mallory = new(Guid.Parse("00000000-0000-0000-0000-0000000000c9"));

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
        new((history, command) => Decider.Decide(history.Count == 0 ? null : Fold.Of<State>(history), command));

    // ── notation, as in the .md ───────────────────────────────────────────────────

    private static IReadOnlyList<MemberShares> Shares(params (MemberId Member, int Shares)[] shares) =>
        [.. shares.Select(s => new MemberShares(s.Member, s.Shares))];

    private static Command ChangeDefaultSplit(string? mode, IReadOnlyList<MemberShares> shares, UserId by) => new(mode, shares, by);

    private static GroupDefaultSplitChanged Changed(string mode, IReadOnlyList<MemberShares> shares, UserId by) => new(mode, shares, by);

    // ── Scenarios ─────────────────────────────────────────────────────────────────

    [Fact]
    public void S1_a_shares_default_only_the_shares_that_are_not_1_are_recorded() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("shares", Shares((M1, 2), (M2, 2), (M3, 1)), Alice))
            .ThenChanged(Changed("shares", Shares((M1, 2), (M2, 2)), Alice));

    [Fact]
    public void S2_an_equal_default_with_someone_left_out() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("equal", Shares((M1, 1), (M2, 1), (M3, 0)), Alice))
            .ThenChanged(Changed("equal", Shares((M3, 0)), Alice));

    [Fact]
    public void S3_an_equal_default_reads_any_share_above_zero_as_one() =>
        Spec.Given([.. Lisbon, Changed("shares", Shares((M1, 2)), Alice)])
            .When(ChangeDefaultSplit("equal", Shares((M1, 3), (M2, 1), (M3, 1)), Alice))
            .ThenChanged(Changed("equal", Shares(), Alice));

    [Fact]
    public void S4_a_member_who_is_not_listed_counts_as_one() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("shares", Shares((M1, 2)), Alice))
            .ThenChanged(Changed("shares", Shares((M1, 2)), Alice));

    [Fact]
    public void S5_shares_are_recorded_in_member_added_order() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("shares", Shares((M3, 0), (M1, 2)), Alice))
            .ThenChanged(Changed("shares", Shares((M1, 2), (M3, 0)), Alice));

    [Fact]
    public void S6_a_default_that_is_the_one_in_force_changes_nothing_and_appends_nothing() =>
        Spec.Given([.. Lisbon, Changed("shares", Shares((M1, 2)), Alice)])
            .When(ChangeDefaultSplit("shares", Shares((M1, 2), (M2, 1), (M3, 1)), Alice))
            .ThenUnchanged("nothing changed");

    [Fact]
    public void S7_the_first_default_if_it_is_the_original_one_changes_nothing() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("equal", Shares((M1, 1), (M2, 1), (M3, 1)), Alice))
            .ThenUnchanged("nothing changed");

    [Fact]
    public void S7b_an_empty_list_is_the_original_default_too() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("equal", Shares(), Alice))
            .ThenUnchanged("nothing changed");

    [Fact]
    public void S8_back_to_the_original_is_a_change_after_another() =>
        Spec.Given([.. Lisbon, Changed("equal", Shares((M3, 0)), Alice)])
            .When(ChangeDefaultSplit("equal", Shares((M1, 1), (M2, 1), (M3, 1)), Alice))
            .ThenChanged(Changed("equal", Shares(), Alice));

    [Fact]
    public void S9_the_mode_is_part_of_the_default() =>
        Spec.Given([.. Lisbon, Changed("equal", Shares((M3, 0)), Alice)])
            .When(ChangeDefaultSplit("shares", Shares((M3, 0)), Alice))
            .ThenChanged(Changed("shares", Shares((M3, 0)), Alice));

    [Fact]
    public void S9b_shares_of_one_each_is_not_the_original_default_it_is_a_mode_change() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("shares", Shares(), Alice))
            .ThenChanged(Changed("shares", Shares(), Alice));

    [Fact]
    public void S10_any_member_may_change_it_and_is_recorded_as_the_actor() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("equal", Shares((M3, 0)), Bob))
            .ThenChanged(Changed("equal", Shares((M3, 0)), Bob));

    [Fact]
    public void S11_the_group_must_exist() =>
        Spec.Given()
            .When(ChangeDefaultSplit("equal", Shares(), Alice))
            .ThenNotFound("group not found");

    [Fact]
    public void S12_only_members_of_the_group_may_change_it() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("equal", Shares((M3, 0)), Mallory))
            .ThenNotFound("group not found");

    [Theory]
    [InlineData("exact")]
    [InlineData("Equal")]
    [InlineData("")]
    [InlineData(null)]
    public void S14_the_mode_must_be_equal_or_shares(string? mode) =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit(mode, Shares(), Alice))
            .ThenRejected("default split must be equal or shares");

    [Fact]
    public void S16_every_member_listed_must_be_a_member_slot_of_the_group() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("shares", Shares((M1, 2), (M9, 1)), Alice))
            .ThenRejected("participant is not a member of the group");

    [Fact]
    public void S17_nobody_is_listed_twice() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("shares", Shares((M1, 2), (M1, 3)), Alice))
            .ThenRejected("a participant appears more than once");

    [Fact]
    public void S18_a_share_is_a_whole_number_zero_or_more() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("shares", Shares((M1, -1)), Alice))
            .ThenRejected("every share must be zero or more");

    [Fact]
    public void S19_someone_must_share_by_default() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("shares", Shares((M1, 0), (M2, 0), (M3, 0)), Alice))
            .ThenRejected("at least one member must share by default");

    [Fact]
    public void S19b_and_so_in_equal() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("equal", Shares((M1, 0), (M2, 0), (M3, 0)), Alice))
            .ThenRejected("at least one member must share by default");

    [Fact]
    public void S20_a_member_not_listed_counts_as_sharing_so_this_is_not_nobody() =>
        Spec.Given([.. Lisbon, new MemberAdded(M4, "Dave", Alice)])
            .When(ChangeDefaultSplit("equal", Shares((M1, 0), (M2, 0), (M3, 0)), Alice))
            .ThenChanged(Changed("equal", Shares((M1, 0), (M2, 0), (M3, 0)), Alice));

    [Fact]
    public void A_member_added_after_a_default_starts_in_it() =>
        Spec.Given([.. Lisbon, Changed("equal", Shares((M1, 0), (M2, 0), (M3, 0)), Alice), new MemberAdded(M4, "Dave", Alice)])
            .When(ChangeDefaultSplit("equal", Shares((M1, 0), (M2, 0), (M3, 0), (M4, 0)), Alice))
            .ThenRejected("at least one member must share by default");

    [Fact]
    public void A_non_member_is_told_nothing_even_by_a_bad_mode() =>
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("exact", Shares((M9, -1)), Mallory))
            .ThenNotFound("group not found");

    [Fact]
    public void The_mode_is_checked_before_the_members()
    {
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("exact", Shares((M9, 1)), Alice))
            .ThenRejected("default split must be equal or shares");
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("shares", Shares((M9, -1)), Alice))
            .ThenRejected("participant is not a member of the group");
    }

    [Fact]
    public void A_listed_one_in_shares_mode_is_not_recorded()
    {
        Spec.Given(Lisbon)
            .When(ChangeDefaultSplit("shares", Shares((M1, 1), (M2, 3)), Alice))
            .ThenChanged(Changed("shares", Shares((M2, 3)), Alice));
    }
}

internal static class DefaultSplitAssertions
{
    /// <summary>Compares the shares by content: the event's list is not a record itself.</summary>
    public static void ThenChanged(this DecideSpec<Command>.WhenStage when, GroupDefaultSplitChanged expected)
    {
        if (when.Decision is not Decision.Accepted accepted)
            throw Xunit.Sdk.FailException.ForFailure($"Expected {expected}, but got {when.Decision}");
        var actual = Assert.IsType<GroupDefaultSplitChanged>(Assert.Single(accepted.Events));
        Assert.Equal((expected.Mode, expected.By), (actual.Mode, actual.By));
        Assert.Equal(expected.Shares, actual.Shares);
    }
}
