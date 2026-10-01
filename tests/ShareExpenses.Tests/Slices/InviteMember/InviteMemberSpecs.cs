using ShareExpenses.Shared;
using ShareExpenses.Slices.InviteMember;
using ShareExpenses.Tests.Specs;
// Only the public events: tests can see every slice's internals, so a namespace
// import would bring in CreateGroup's own Command too.
using GroupCreated = ShareExpenses.Slices.CreateGroup.GroupCreated;
using MemberAdded = ShareExpenses.Slices.CreateGroup.MemberAdded;
using MemberClaimed = ShareExpenses.Slices.CreateGroup.MemberClaimed;

namespace ShareExpenses.Tests.Slices.InviteMember;

/// <summary>
/// docs/event-model/slice-03-invite-member.md, line for line. Selected scenarios run
/// against a real store in <see cref="InviteMemberIntegrationTests"/>.
/// </summary>
public class InviteMemberSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly MemberId M9 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b9"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly UserId Bob = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));
    private static readonly UserId Mallory = new(Guid.Parse("00000000-0000-0000-0000-0000000000c9"));
    private static readonly string H0 = InviteToken.Hash("t0");
    private static readonly string H1 = InviteToken.Hash("t1");

    /// <summary>The group as slice 1 leaves it, plus a placeholder "Bob".</summary>
    private static readonly object[] Lisbon =
    [
        new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
        new MemberAdded(M1, "Alice", Alice),
        new MemberClaimed(M1, Alice),
        new MemberAdded(M2, "Bob", Alice),
    ];

    private static readonly DecideSpec<Command> Spec = new((history, command) => Decider.Decide(Fold(history), command));

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
            MemberInvited invited => state!.Apply(invited),
            _ => throw new InvalidOperationException($"InviteMember's State does not fold {e.GetType().Name}"),
        });

    private static Command InviteMember(MemberId member, string? email, UserId by) => new(G1, member, email, H1, by);

    [Fact]
    public void S1_invites_a_placeholder_member() =>
        Spec.Given(Lisbon)
            .When(InviteMember(M2, "bob@example.com", Alice))
            .Then(new MemberInvited(M2, "bob@example.com", H1, Alice));

    [Fact]
    public void S2_trims_the_email_and_keeps_its_case() =>
        Spec.Given(Lisbon)
            .When(InviteMember(M2, "  Bob@Example.com ", Alice))
            .Then(new MemberInvited(M2, "Bob@Example.com", H1, Alice));

    [Fact]
    public void S3_the_group_must_exist() =>
        Spec.Given()
            .When(InviteMember(M2, "bob@example.com", Alice))
            .ThenRejected("group not found");

    [Fact]
    public void S4_only_members_of_the_group_may_invite() =>
        Spec.Given(Lisbon)
            .When(InviteMember(M2, "bob@example.com", Mallory))
            .ThenRejected("group not found");

    [Fact]
    public void S5_the_slot_must_exist_in_the_group() =>
        Spec.Given(Lisbon)
            .When(InviteMember(M9, "bob@example.com", Alice))
            .ThenRejected("member not found");

    [Fact]
    public void S6_a_slot_that_has_already_joined_cannot_be_invited() =>
        Spec.Given(Lisbon)
            .When(InviteMember(M1, "alice@example.com", Alice))
            .ThenRejected("member has already joined");

    [Theory]
    [InlineData("bob")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("@example.com")]
    [InlineData("bob@")]
    [InlineData("bob@@example.com")]
    [InlineData("bob@exa@mple.com")]
    [InlineData("bob smith@example.com")]
    public void S7_rejects_an_address_that_is_not_plausibly_an_email(string? email) =>
        Spec.Given(Lisbon)
            .When(InviteMember(M2, email, Alice))
            .ThenRejected("email is not a valid address");

    [Fact]
    public void S7_rejects_an_address_longer_than_254_characters() =>
        Spec.Given(Lisbon)
            .When(InviteMember(M2, new string('b', 243) + "@example.com", Alice))
            .ThenRejected("email is not a valid address");

    [Fact]
    public void S7_accepts_an_address_of_exactly_254_characters() =>
        Spec.Given(Lisbon)
            .When(InviteMember(M2, new string('b', 242) + "@example.com", Alice))
            .Then(new MemberInvited(M2, new string('b', 242) + "@example.com", H1, Alice));

    [Fact]
    public void S8_re_inviting_replaces_the_previous_invite() =>
        Spec.Given([.. Lisbon, new MemberInvited(M2, "bob@old.com", H0, Alice)])
            .When(InviteMember(M2, "bob@new.com", Alice))
            .Then(new MemberInvited(M2, "bob@new.com", H1, Alice));

    [Fact]
    public void S9_an_email_cannot_be_invited_to_two_slots() =>
        Spec.Given([.. Lisbon, new MemberAdded(M3, "Bobby", Alice), new MemberInvited(M2, "bob@example.com", H0, Alice)])
            .When(InviteMember(M3, "BOB@example.com", Alice))
            .ThenRejected("that email is already invited as Bob");

    [Fact]
    public void S10_re_inviting_the_same_slot_with_the_same_email_is_fine() =>
        Spec.Given([.. Lisbon, new MemberInvited(M2, "bob@example.com", H0, Alice)])
            .When(InviteMember(M2, "bob@example.com", Alice))
            .Then(new MemberInvited(M2, "bob@example.com", H1, Alice));

    [Fact]
    public void S11_an_email_that_has_joined_cannot_be_invited_to_another_slot() =>
        Spec.Given(
            [
                .. Lisbon,
                new MemberInvited(M2, "bob@example.com", H0, Alice),
                new MemberClaimed(M2, Bob),
                new MemberAdded(M3, "Bobby", Alice),
            ])
            .When(InviteMember(M3, "bob@example.com", Alice))
            .ThenRejected("bob@example.com has already joined as Bob");

    [Fact]
    public void A_non_member_learns_nothing_from_validation() =>
        Spec.Given(Lisbon)
            .When(InviteMember(M9, "not-an-email", Mallory))
            .ThenRejected("group not found");

    [Fact]
    public void Any_member_may_invite_and_is_recorded_as_the_actor() =>
        Spec.Given([.. Lisbon, new MemberAdded(M3, "Carol", Alice), new MemberClaimed(M3, Bob)])
            .When(InviteMember(M2, "bob@example.com", Bob))
            .Then(new MemberInvited(M2, "bob@example.com", H1, Bob));
}
