using SplitIt.Shared;
using SplitIt.Slices.AddMember;
using SplitIt.Tests.Specs;
// Only the public events: tests can see every slice's internals, so a namespace
// import would bring in CreateGroup's own Command too.
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;
using GroupRenamed = SplitIt.Slices.RenameGroup.GroupRenamed;
using GroupArchived = SplitIt.Slices.ArchiveGroup.GroupArchived;

namespace SplitIt.Tests.Slices.AddMember;

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
    private static readonly InviteId I0 = new(Guid.Parse("00000000-0000-0000-0000-0000000000d0"));
    private static readonly InviteId I1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000d1"));

    /// <summary>"Now" for every scenario; a new invite expires 30 days later.</summary>
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T30 = T0.AddDays(30);

    /// <summary>The deadline of some earlier invite in a Given; deciding never reads it.</summary>
    private static readonly DateTimeOffset Earlier = T0.AddDays(20);

    /// <summary>The group as CreateGroup leaves it.</summary>
    private static readonly object[] Lisbon =
    [
        new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
        new MemberAdded(M1, "Alice", Alice),
        new MemberClaimed(M1, Alice),
    ];

    /// <summary>The next member slot the handler would be given; pinned per scenario.</summary>
    private MemberId _next = M2;

    private DecideSpec<Command> Spec => new((history, command) => Decider.Decide(Fold.Of<State>(history), command));

    private Command AddMember(string? displayName, UserId by) => new(_next, displayName, null, I1, T0, by);

    private Command AddMember(
        string? displayName, string? email, UserId by, UserId? emailHolder = null, params MemberId[] invitedTo) =>
        new(_next, displayName, email, I1, T0, by, emailHolder, invitedTo.ToHashSet());

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
            .ThenNotFound("group not found");

    [Fact]
    public void S4_only_members_of_the_group_may_add() =>
        Spec.Given(Lisbon)
            .When(AddMember("Bob", Mallory))
            .ThenNotFound("group not found");

    [Fact]
    public void S5_an_unclaimed_placeholder_confers_no_membership() =>
        Spec.Given([.. Lisbon, new MemberAdded(M2, "Bob", Alice)])
            .When(AddMember("Carol", Bob))
            .ThenNotFound("group not found");

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
    public void S10_with_an_email_adds_and_invites_in_one() =>
        Spec.Given(Lisbon)
            .When(AddMember("Bob", "bob@example.com", Alice))
            .Then(new MemberAdded(M2, "Bob", Alice), new MemberInvited(M2, I1, T30, Alice));

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void S11_a_blank_email_is_no_email(string? email) =>
        Spec.Given(Lisbon)
            .When(AddMember("Bob", email, Alice))
            .Then(new MemberAdded(M2, "Bob", Alice));

    [Fact]
    public void S12_an_email_that_is_not_plausibly_one_adds_no_one() =>
        Spec.Given(Lisbon)
            .When(AddMember("Bob", "bob", Alice))
            .ThenRejected("email is not a valid address");

    [Fact]
    public void S13_the_address_of_a_user_already_in_the_group_adds_no_one() =>
        Spec.Given(Lisbon)
            .When(AddMember("Bob", "alice@example.com", Alice, emailHolder: Alice))
            .ThenRejected("alice@example.com has already joined as Alice");

    [Fact]
    public void S14_an_address_with_an_open_invite_on_another_slot_adds_no_one()
    {
        _next = M3;
        Spec.Given([.. Lisbon, new MemberAdded(M2, "Bobby", Alice), new MemberInvited(M2, I0, Earlier, Alice)])
            .When(AddMember("Bob", "BOB@example.com", Alice, invitedTo: M2))
            .ThenRejected("that email is already invited as Bobby");
    }

    [Fact]
    public void S15_an_invite_to_a_slot_since_claimed_no_longer_holds_its_address()
    {
        _next = M3;
        Spec.Given([.. Lisbon, new MemberAdded(M2, "Bobby", Alice), new MemberInvited(M2, I0, Earlier, Alice), new MemberClaimed(M2, Bob)])
            .When(AddMember("Bob", "bob@example.com", Alice, invitedTo: M2))
            .Then(new MemberAdded(M3, "Bob", Alice), new MemberInvited(M3, I1, T30, Alice));
    }

    [Fact]
    public void S16_the_name_is_checked_before_the_email() =>
        Spec.Given(Lisbon)
            .When(AddMember("", "bob", Alice))
            .ThenRejected("name is required");

    [Fact]
    public void S17_a_member_id_already_added_is_not_added_twice() =>
        Spec.Given([.. Lisbon, new MemberAdded(M2, "Bob", Alice)])
            .When(AddMember("Bob", Alice))
            .ThenAlreadyRecorded("member already added");

    [Fact]
    public void A_non_member_learns_nothing_from_validation() =>
        Spec.Given(Lisbon)
            .When(AddMember("   ", Mallory))
            .ThenNotFound("group not found");

    [Fact]
    public void The_limit_is_inclusive_and_measured_after_trimming() =>
        Spec.Given(Lisbon)
            .When(AddMember($"  {new string('x', 50)}  ", Alice))
            .Then(new MemberAdded(M2, new string('x', 50), Alice));

    [Fact]
    public void A_renamed_group_is_folded_under_its_new_name() =>
        Assert.Equal("Porto trip", Fold.Of<State>([.. Lisbon, new GroupRenamed("Porto trip", Bob)])!.GroupName);

    [Fact]
    public void S18_an_archived_group_takes_no_new_members() =>
        Spec.Given([.. Lisbon, new GroupArchived(Alice)])
            .When(AddMember("Dave", Alice))
            .ThenRejected("group is archived");
}
