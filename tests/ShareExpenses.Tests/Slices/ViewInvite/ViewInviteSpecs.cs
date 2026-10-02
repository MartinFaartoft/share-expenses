using ShareExpenses.Shared;
using ShareExpenses.Slices.ViewInvite;
using ShareExpenses.Tests.Specs;
// Only the public events: tests can see every slice's internals, so namespace
// imports would bring in other slices' Command, State and Endpoint too.
using GroupCreated = ShareExpenses.Slices.CreateGroup.GroupCreated;
using MemberAdded = ShareExpenses.Slices.CreateGroup.MemberAdded;
using MemberClaimed = ShareExpenses.Slices.CreateGroup.MemberClaimed;
using MemberInvited = ShareExpenses.Slices.InviteMember.MemberInvited;

namespace ShareExpenses.Tests.Slices.ViewInvite;

/// <summary>
/// docs/event-model/slice-04-view-invite.md, line for line. The same fold runs
/// against a real store, through HTTP, in <see cref="ViewInviteIntegrationTests"/>.
/// </summary>
public class ViewInviteSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly UserId Bob = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));
    private static readonly UserId Carol = new(Guid.Parse("00000000-0000-0000-0000-0000000000c3"));

    private const string T1 = "t1", T2 = "t2", TX = "tX";
    private static readonly string H1 = InviteToken.Hash(T1);
    private static readonly string H2 = InviteToken.Hash(T2);

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Day(int n) => T0.AddDays(n);

    /// <summary>The group with Bob invited at t0, link live until t0+30d.</summary>
    private static readonly object[] Lisbon =
    [
        new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
        new MemberAdded(M1, "Alice", Alice),
        new MemberClaimed(M1, Alice),
        new MemberAdded(M2, "Bob", Alice),
        new MemberInvited(M2, H1, Day(30), Alice),
    ];

    private static readonly InviteReadModel BobByAlice = new("Lisbon trip", "Bob", "Alice");

    private static readonly ReadSpec<Query, InviteReadModel> Spec =
        new((history, query) => Reader.Read(Fold.Of<State>(history), query));

    private static Query ViewInvite(string? token, DateTimeOffset at) => new(token, at);

    [Fact]
    public void S1_shows_a_live_invite() =>
        Spec.Given(Lisbon)
            .When(ViewInvite(T1, at: Day(1)))
            .Then(BobByAlice);

    [Theory]
    [InlineData(TX)]
    [InlineData("")]
    [InlineData(null)]
    public void S2_a_wrong_token_finds_nothing(string? token) =>
        Spec.Given(Lisbon)
            .When(ViewInvite(token, at: Day(1)))
            .ThenNotFound();

    [Fact]
    public void S2_the_hash_itself_is_not_a_token() =>
        Spec.Given(Lisbon)
            .When(ViewInvite(H1, at: Day(1)))
            .ThenNotFound();

    [Fact]
    public void S3_an_unknown_group_finds_nothing() =>
        Spec.Given()
            .When(ViewInvite(T1, at: Day(1)))
            .ThenNotFound();

    [Fact]
    public void S4_an_invite_is_dead_at_its_deadline() =>
        Spec.Given(Lisbon)
            .When(ViewInvite(T1, at: Day(30)))
            .ThenNotFound();

    [Fact]
    public void S4_and_alive_just_before_it() =>
        Spec.Given(Lisbon)
            .When(ViewInvite(T1, at: Day(30).AddSeconds(-1)))
            .Then(BobByAlice);

    [Fact]
    public void S5_re_inviting_retires_the_previous_link() =>
        Spec.Given([.. Lisbon, new MemberInvited(M2, H2, Day(31), Alice)])
            .When(ViewInvite(T1, at: Day(1)))
            .ThenNotFound();

    [Fact]
    public void S6_the_new_link_has_its_own_deadline() =>
        Spec.Given([.. Lisbon, new MemberInvited(M2, H2, Day(59), Alice)])
            .When(ViewInvite(T2, at: Day(40)))
            .Then(BobByAlice);

    [Fact]
    public void S7_a_claimed_slots_invite_is_used_up() =>
        Spec.Given([.. Lisbon, new MemberClaimed(M2, Bob)])
            .When(ViewInvite(T1, at: Day(1)))
            .ThenNotFound();

    [Fact]
    public void S8_the_inviter_is_named_by_their_slot_in_this_group() =>
        Spec.Given(
            [
                .. Lisbon,
                new MemberAdded(M3, "Carol", Alice),
                new MemberClaimed(M3, Carol),
                new MemberInvited(M2, H2, Day(30), Carol),
            ])
            .When(ViewInvite(T2, at: Day(1)))
            .Then(new InviteReadModel("Lisbon trip", "Bob", "Carol"));

    [Fact]
    public void Each_open_invite_answers_only_to_its_own_token() =>
        Spec.Given([.. Lisbon, new MemberAdded(M3, "Carol", Alice), new MemberInvited(M3, H2, Day(30), Alice)])
            .When(ViewInvite(T2, at: Day(1)))
            .Then(new InviteReadModel("Lisbon trip", "Carol", "Alice"));
}
