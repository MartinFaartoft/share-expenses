using SplitIt.Shared;
using SplitIt.Slices.AcceptInvite;
using SplitIt.Tests.Specs;
// Only the public events: tests can see every slice's internals, so namespace
// imports would bring in other slices' Command, State and Endpoint too.
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;
using MemberInvited = SplitIt.Slices.AddMember.MemberInvited;
using GroupArchived = SplitIt.Slices.ArchiveGroup.GroupArchived;

namespace SplitIt.Tests.Slices.AcceptInvite;

/// <summary>
/// docs/event-model/slice-05-accept-invite.md, line for line. The spec's WITH line is
/// the looked-up <c>invitedAs</c> carried on the command. Selected scenarios run
/// against a real store, and over HTTP, in <see cref="AcceptInviteIntegrationTests"/>.
/// </summary>
public class AcceptInviteSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly UserId Bob = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));
    private static readonly UserId Carol = new(Guid.Parse("00000000-0000-0000-0000-0000000000c3"));
    private static readonly InviteId I1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000d1"));
    private static readonly InviteId I2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000d2"));

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Day(int n) => T0.AddDays(n);

    /// <summary>Bob invited at t0, live until t0+30d.</summary>
    private static readonly object[] Lisbon =
    [
        new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
        new MemberAdded(M1, "Alice", Alice),
        new MemberClaimed(M1, Alice),
        new MemberAdded(M2, "Bob", Alice),
        new MemberInvited(M2, I1, Day(30), Alice),
    ];

    private static readonly DecideSpec<Command> Spec =
        new((history, command) => Decider.Decide(Fold.Of<State>(history), command));

    private static Command AcceptInvite(UserId by, DateTimeOffset at, params InviteId[] invitedAs) =>
        new(at, by, invitedAs.ToHashSet());

    [Fact]
    public void S1_claims_the_slot_invited_at_the_users_address() =>
        Spec.Given(Lisbon)
            .When(AcceptInvite(Bob, at: Day(1), I1))
            .Then(new MemberClaimed(M2, Bob));

    [Fact]
    public void S2_another_address_claims_nothing() =>
        Spec.Given(Lisbon)
            .When(AcceptInvite(Carol, at: Day(1)))
            .ThenNotFound("invite not found");

    [Fact]
    public void S3_an_unknown_group() =>
        Spec.Given()
            .When(AcceptInvite(Bob, at: Day(1), I1))
            .ThenNotFound("invite not found");

    [Fact]
    public void S4_an_invite_is_dead_at_its_deadline() =>
        Spec.Given(Lisbon)
            .When(AcceptInvite(Bob, at: Day(30), I1))
            .ThenNotFound("invite not found");

    [Fact]
    public void S4_and_claimable_just_before_it() =>
        Spec.Given(Lisbon)
            .When(AcceptInvite(Bob, at: Day(30).AddSeconds(-1), I1))
            .Then(new MemberClaimed(M2, Bob));

    [Fact]
    public void S5_only_the_slots_current_invite_counts() =>
        Spec.Given([.. Lisbon, new MemberInvited(M2, I2, Day(31), Alice)])
            .When(AcceptInvite(Bob, at: Day(1), I1))
            .ThenNotFound("invite not found");

    [Fact]
    public void S6_a_claimed_slots_invite_is_used_up() =>
        Spec.Given([.. Lisbon, new MemberClaimed(M2, Carol)])
            .When(AcceptInvite(Bob, at: Day(1), I1))
            .ThenNotFound("invite not found");

    [Fact]
    public void S7_a_member_cannot_claim_a_second_slot() =>
        Spec.Given([.. Lisbon, new MemberAdded(M3, "Bobby", Alice), new MemberInvited(M3, I2, Day(30), Alice)])
            .When(AcceptInvite(Alice, at: Day(1), I2))
            .ThenAlreadyMember("you're already in this group as Alice");

    [Fact]
    public void S8_joining_again_after_joining() =>
        Spec.Given([.. Lisbon, new MemberClaimed(M2, Bob)])
            .When(AcceptInvite(Bob, at: Day(1)))
            .ThenAlreadyMember("you're already in this group as Bob");

    [Fact]
    public void S9_two_slots_invited_at_one_address_the_newest_invite_wins() =>
        Spec.Given([.. Lisbon, new MemberAdded(M3, "Bobby", Alice), new MemberInvited(M3, I2, Day(31), Alice)])
            .When(AcceptInvite(Bob, at: Day(1), I1, I2))
            .Then(new MemberClaimed(M3, Bob));

    [Fact]
    public void S9_whichever_order_the_lookup_lists_them_in() =>
        Spec.Given([.. Lisbon, new MemberAdded(M3, "Bobby", Alice), new MemberInvited(M3, I2, Day(31), Alice)])
            .When(AcceptInvite(Bob, at: Day(1), I2, I1))
            .Then(new MemberClaimed(M3, Bob));

    [Fact]
    public void Membership_is_checked_before_any_invite() =>
        Spec.Given(Lisbon)
            .When(AcceptInvite(Alice, at: Day(60)))
            .ThenAlreadyMember("you're already in this group as Alice");

    [Fact]
    public void S10_an_invite_into_an_archived_group_is_dead() =>
        Spec.Given([.. Lisbon, new GroupArchived(Alice)])
            .When(AcceptInvite(Bob, Day(1), I1))
            .ThenNotFound("invite not found");

    [Fact]
    public void An_archived_group_is_dead_to_a_member_already_in_it() =>
        Spec.Given([.. Lisbon, new GroupArchived(Alice)])
            .When(AcceptInvite(Alice, Day(1), I1))
            .ThenNotFound("invite not found");
}
