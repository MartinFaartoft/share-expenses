using ShareExpenses.Shared;
using ShareExpenses.Slices.AcceptInvite;
using ShareExpenses.Tests.Specs;
// Only the public events: tests can see every slice's internals, so namespace
// imports would bring in other slices' Command, State and Endpoint too.
using GroupCreated = ShareExpenses.Slices.CreateGroup.GroupCreated;
using MemberAdded = ShareExpenses.Slices.CreateGroup.MemberAdded;
using MemberClaimed = ShareExpenses.Slices.CreateGroup.MemberClaimed;
using MemberInvited = ShareExpenses.Slices.InviteMember.MemberInvited;

namespace ShareExpenses.Tests.Slices.AcceptInvite;

/// <summary>
/// docs/event-model/slice-05-claim-member.md, line for line. Selected scenarios run
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
    private static readonly UserId Dave = new(Guid.Parse("00000000-0000-0000-0000-0000000000c4"));

    private const string T1 = "t1", T2 = "t2", TX = "tX";
    private static readonly string H1 = InviteToken.Hash(T1);
    private static readonly string H2 = InviteToken.Hash(T2);

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Day(int n) => T0.AddDays(n);

    /// <summary>Bob invited at t0, link live until t0+30d.</summary>
    private static readonly object[] Lisbon =
    [
        new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
        new MemberAdded(M1, "Alice", Alice),
        new MemberClaimed(M1, Alice),
        new MemberAdded(M2, "Bob", Alice),
        new MemberInvited(M2, H1, Day(30), Alice),
    ];

    private static readonly DecideSpec<Command> Spec =
        new((history, command) => Decider.Decide(Fold.Of<State>(history), command));

    private static Command AcceptInvite(string? token, UserId by, DateTimeOffset at) => new(token, at, by);

    [Fact]
    public void S1_claims_the_invited_slot() =>
        Spec.Given(Lisbon)
            .When(AcceptInvite(T1, Bob, at: Day(1)))
            .Then(new MemberClaimed(M2, Bob));

    [Fact]
    public void S2_a_forwarded_link_works_for_whoever_holds_it() =>
        Spec.Given(Lisbon)
            .When(AcceptInvite(T1, Carol, at: Day(1)))
            .Then(new MemberClaimed(M2, Carol));

    [Theory]
    [InlineData(TX)]
    [InlineData("")]
    [InlineData(null)]
    public void S3_a_wrong_token_claims_nothing(string? token) =>
        Spec.Given(Lisbon)
            .When(AcceptInvite(token, Bob, at: Day(1)))
            .ThenNotFound("invite not found");

    [Fact]
    public void S3_the_hash_itself_is_not_a_token() =>
        Spec.Given(Lisbon)
            .When(AcceptInvite(H1, Bob, at: Day(1)))
            .ThenNotFound("invite not found");

    [Fact]
    public void S4_an_unknown_group() =>
        Spec.Given()
            .When(AcceptInvite(T1, Bob, at: Day(1)))
            .ThenNotFound("invite not found");

    [Fact]
    public void S5_a_link_is_dead_at_its_deadline() =>
        Spec.Given(Lisbon)
            .When(AcceptInvite(T1, Bob, at: Day(30)))
            .ThenNotFound("invite not found");

    [Fact]
    public void S5_and_claimable_just_before_it() =>
        Spec.Given(Lisbon)
            .When(AcceptInvite(T1, Bob, at: Day(30).AddSeconds(-1)))
            .Then(new MemberClaimed(M2, Bob));

    [Fact]
    public void S6_a_superseded_link_claims_nothing() =>
        Spec.Given([.. Lisbon, new MemberInvited(M2, H2, Day(31), Alice)])
            .When(AcceptInvite(T1, Bob, at: Day(1)))
            .ThenNotFound("invite not found");

    [Fact]
    public void S7_a_used_link_claims_nothing() =>
        Spec.Given([.. Lisbon, new MemberClaimed(M2, Carol)])
            .When(AcceptInvite(T1, Dave, at: Day(1)))
            .ThenNotFound("invite not found");

    [Fact]
    public void S8_a_member_cannot_claim_a_second_slot() =>
        Spec.Given([.. Lisbon, new MemberAdded(M3, "Bobby", Alice), new MemberInvited(M3, H2, Day(30), Alice)])
            .When(AcceptInvite(T2, Alice, at: Day(1)))
            .ThenAlreadyMember("you're already in this group as Alice");

    [Fact]
    public void S9_tapping_the_link_again_after_joining() =>
        Spec.Given([.. Lisbon, new MemberClaimed(M2, Bob)])
            .When(AcceptInvite(T1, Bob, at: Day(1)))
            .ThenAlreadyMember("you're already in this group as Bob");

    [Fact]
    public void Membership_is_checked_before_the_token() =>
        Spec.Given(Lisbon)
            .When(AcceptInvite(TX, Alice, at: Day(60)))
            .ThenAlreadyMember("you're already in this group as Alice");

    [Fact]
    public void Each_open_invite_answers_only_to_its_own_token() =>
        Spec.Given([.. Lisbon, new MemberAdded(M3, "Carol", Alice), new MemberInvited(M3, H2, Day(30), Alice)])
            .When(AcceptInvite(T2, Carol, at: Day(1)))
            .Then(new MemberClaimed(M3, Carol));
}
