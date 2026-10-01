using ShareExpenses.Shared;
using ShareExpenses.Slices.AddMember;
using ShareExpenses.Tests.Specs;
// Only the public events: tests can see every slice's internals, so a namespace
// import would bring in CreateGroup's own Command too.
using GroupCreated = ShareExpenses.Slices.CreateGroup.GroupCreated;
using MemberAdded = ShareExpenses.Slices.CreateGroup.MemberAdded;
using MemberClaimed = ShareExpenses.Slices.CreateGroup.MemberClaimed;

namespace ShareExpenses.Tests.Slices.AddMember;

/// <summary>
/// docs/event-model/slice-02-add-member.md, line for line. The same scenarios run
/// against a real store in <see cref="AddMemberIntegrationTests"/>.
/// </summary>
public class AddMemberSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly UserId Bob = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));
    private static readonly UserId Mallory = new(Guid.Parse("00000000-0000-0000-0000-0000000000c9"));

    /// <summary>The group as slice 1 leaves it.</summary>
    private static readonly object[] Lisbon =
    [
        new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
        new MemberAdded(M1, "Alice", Alice),
        new MemberClaimed(M1, Alice),
    ];

    /// <summary>The next member slot the handler would be given; pinned per scenario.</summary>
    private MemberId _next = M2;

    private DecideSpec<Command> Spec => new((history, command) => Decider.Decide(Fold(history), command, _next));

    /// <summary>
    /// Folds history with the same Create/Apply methods Marten calls. Unknown events
    /// throw, so a new event in a Given forces this fold — and the State — to be updated.
    /// </summary>
    private static State? Fold(IReadOnlyList<object> history) =>
        history.Aggregate((State?)null, (state, e) => e switch
        {
            GroupCreated created => State.Create(created),
            MemberAdded added => state!.Apply(added),
            MemberClaimed claimed => state!.Apply(claimed),
            _ => throw new InvalidOperationException($"AddMember's State does not fold {e.GetType().Name}"),
        });

    private static Command AddMember(string? displayName, UserId by) => new(G1, displayName, by);

    [Fact]
    public void S1_adds_a_placeholder_member_by_name_alone() =>
        Spec.Given(Lisbon)
            .When(AddMember("Bob", Alice))
            .Then(new MemberAdded(M2, "Bob", Alice));

    [Fact]
    public void S2_any_member_may_add_and_is_recorded_as_the_actor()
    {
        _next = M3;
        Spec.Given([.. Lisbon, new MemberAdded(M2, "Bob", Alice), new MemberClaimed(M2, Bob)])
            .When(AddMember("Carol", Bob))
            .Then(new MemberAdded(M3, "Carol", Bob));
    }

    [Fact]
    public void S3_the_group_must_exist() =>
        Spec.Given()
            .When(AddMember("Bob", Alice))
            .ThenRejected("group not found");

    [Fact]
    public void S4_only_members_of_the_group_may_add() =>
        Spec.Given(Lisbon)
            .When(AddMember("Bob", Mallory))
            .ThenRejected("group not found");

    [Fact]
    public void S5_an_unclaimed_placeholder_confers_no_membership() =>
        Spec.Given([.. Lisbon, new MemberAdded(M2, "Bob", Alice)])
            .When(AddMember("Carol", Bob))
            .ThenRejected("group not found");

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void S6_rejects_a_blank_name(string? name) =>
        Spec.Given(Lisbon)
            .When(AddMember(name, Alice))
            .ThenRejected("name is required");

    [Theory]
    [InlineData("Alice")]
    [InlineData(" alice ")]
    [InlineData("ALICE")]
    public void S7_rejects_a_name_already_used_in_the_group(string name) =>
        Spec.Given(Lisbon)
            .When(AddMember(name, Alice))
            .ThenRejected("a member with that name already exists");

    [Fact]
    public void S8_rejects_a_name_longer_than_50_characters() =>
        Spec.Given(Lisbon)
            .When(AddMember(new string('x', 51), Alice))
            .ThenRejected("name must be at most 50 characters");

    [Fact]
    public void S9_trims_the_name() =>
        Spec.Given(Lisbon)
            .When(AddMember("  Bob ", Alice))
            .Then(new MemberAdded(M2, "Bob", Alice));

    [Fact]
    public void A_non_member_learns_nothing_from_validation() =>
        Spec.Given(Lisbon)
            .When(AddMember("   ", Mallory))
            .ThenRejected("group not found");

    [Fact]
    public void The_limit_is_inclusive_and_measured_after_trimming() =>
        Spec.Given(Lisbon)
            .When(AddMember($"  {new string('x', 50)}  ", Alice))
            .Then(new MemberAdded(M2, new string('x', 50), Alice));
}
