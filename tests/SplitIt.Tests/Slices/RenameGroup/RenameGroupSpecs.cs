using SplitIt.Shared;
using SplitIt.Slices.RenameGroup;
using SplitIt.Tests.Specs;
// Only the public events: tests can see every slice's internals, so a namespace
// import would bring in CreateGroup's own Command too.
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;

namespace SplitIt.Tests.Slices.RenameGroup;

/// <summary>
/// docs/event-model/slice-14-rename-group.md, line for line (scenario 10, the archived
/// group, is built with Archive group). Selected scenarios run against a real store,
/// through HTTP, in <see cref="RenameGroupIntegrationTests"/>.
/// </summary>
public class RenameGroupSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
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
        new((history, command) => Decider.Decide(
            history.Count == 0 ? null : Fold.Of<State>(history, ignoring: typeof(MemberAdded)), command));

    [Fact]
    public void S1_renames_the_group() =>
        Spec.Given(Lisbon)
            .When(new Command("Porto trip", Alice))
            .Then(new GroupRenamed("Porto trip", Alice));

    [Fact]
    public void S2_the_name_is_trimmed() =>
        Spec.Given(Lisbon)
            .When(new Command("  Porto trip  ", Alice))
            .Then(new GroupRenamed("Porto trip", Alice));

    [Fact]
    public void S3_any_member_may_rename_it_and_is_recorded_as_the_actor() =>
        Spec.Given(Lisbon)
            .When(new Command("Porto trip", Bob))
            .Then(new GroupRenamed("Porto trip", Bob));

    [Fact]
    public void S4_a_group_can_be_renamed_again() =>
        Spec.Given([.. Lisbon, new GroupRenamed("Porto trip", Bob)])
            .When(new Command("Algarve", Alice))
            .Then(new GroupRenamed("Algarve", Alice));

    [Fact]
    public void S5_the_same_name_changes_nothing_and_appends_nothing() =>
        Spec.Given(Lisbon)
            .When(new Command("Lisbon trip", Alice))
            .ThenUnchanged("nothing changed");

    [Fact]
    public void S5b_the_same_name_with_padding_is_the_same_name() =>
        Spec.Given(Lisbon)
            .When(new Command("  Lisbon trip ", Alice))
            .ThenUnchanged("nothing changed");

    [Fact]
    public void S6_the_name_is_compared_with_the_current_one_not_the_first() =>
        Spec.Given([.. Lisbon, new GroupRenamed("Porto trip", Bob)])
            .When(new Command("Lisbon trip", Alice))
            .Then(new GroupRenamed("Lisbon trip", Alice));

    [Fact]
    public void S6b_the_current_name_is_the_same_name() =>
        Spec.Given([.. Lisbon, new GroupRenamed("Porto trip", Bob)])
            .When(new Command("Porto trip", Alice))
            .ThenUnchanged("nothing changed");

    [Fact]
    public void S7_a_change_of_case_is_a_change() =>
        Spec.Given(Lisbon)
            .When(new Command("lisbon trip", Alice))
            .Then(new GroupRenamed("lisbon trip", Alice));

    [Fact]
    public void S8_the_group_must_exist() =>
        Spec.Given()
            .When(new Command("Porto trip", Alice))
            .ThenNotFound("group not found");

    [Fact]
    public void S9_only_members_may_rename_it() =>
        Spec.Given(Lisbon)
            .When(new Command("Porto trip", Mallory))
            .ThenNotFound("group not found");

    [Fact]
    public void An_unclaimed_placeholder_confers_no_right_to_rename() =>
        Spec.Given(Lisbon)
            .When(new Command("Porto trip", new UserId(Guid.Parse("00000000-0000-0000-0000-0000000000c3"))))
            .ThenNotFound("group not found");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void S11_a_name_is_required(string? name) =>
        Spec.Given(Lisbon)
            .When(new Command(name, Alice))
            .ThenRejected("name is required");

    [Fact]
    public void S12_a_name_is_at_most_100_characters() =>
        Spec.Given(Lisbon)
            .When(new Command(new string('x', 101), Alice))
            .ThenRejected("name must be at most 100 characters");

    [Fact]
    public void S13_exactly_100_characters_is_fine() =>
        Spec.Given(Lisbon)
            .When(new Command(new string('x', 100), Alice))
            .Then(new GroupRenamed(new string('x', 100), Alice));

    [Fact]
    public void The_length_counts_what_is_seen_not_code_units() =>
        Spec.Given(Lisbon)
            .When(new Command(string.Concat(Enumerable.Repeat("🌍", 100)), Alice))
            .Then(new GroupRenamed(string.Concat(Enumerable.Repeat("🌍", 100)), Alice));

    [Fact]
    public void A_non_member_is_told_nothing_even_by_a_blank_name() =>
        Spec.Given(Lisbon)
            .When(new Command("", Mallory))
            .ThenNotFound("group not found");

    [Fact]
    public void The_name_is_checked_before_it_is_compared() =>
        Spec.Given(Lisbon)
            .When(new Command(new string('x', 101), Alice))
            .ThenRejected("name must be at most 100 characters");
}
