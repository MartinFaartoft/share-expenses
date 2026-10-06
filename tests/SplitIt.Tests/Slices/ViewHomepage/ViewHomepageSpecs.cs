using SplitIt.Shared;
using SplitIt.Slices.ViewHomepage;
using SplitIt.Tests.Specs;
// Only the public events: tests can see every slice's internals, so namespace
// imports would bring in other slices' Command, State and Endpoint too.
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;
using MemberInvited = SplitIt.Slices.AddMember.MemberInvited;

namespace SplitIt.Tests.Slices.ViewHomepage;

/// <summary>
/// docs/event-model/slice-04-view-homepage.md, line for line. Several streams per
/// scenario, so not <see cref="ReadSpec{TQuery,TResult}"/>: <see cref="Read"/> folds
/// each given group and reads. The same fold runs against a real store, through
/// HTTP, in <see cref="ViewHomepageIntegrationTests"/>.
/// </summary>
public class ViewHomepageSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly GroupId G2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a2"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly MemberId M2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly MemberId M3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"));
    private static readonly MemberId M4 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b4"));
    private static readonly MemberId M5 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b5"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly UserId Bob = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));
    private static readonly UserId Carol = new(Guid.Parse("00000000-0000-0000-0000-0000000000c3"));
    private static readonly InviteId I1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000d1"));
    private static readonly InviteId I2 = new(Guid.Parse("00000000-0000-0000-0000-0000000000d2"));

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Day(int n) => T0.AddDays(n);

    /// <summary>g1 with Bob invited at t0, live until t0+30d.</summary>
    private static readonly object[] Lisbon =
    [
        new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
        new MemberAdded(M1, "Alice", Alice),
        new MemberClaimed(M1, Alice),
        new MemberAdded(M2, "Bob", Alice),
        new MemberInvited(M2, I1, Day(30), Alice),
    ];

    /// <summary>The lookup: bob's address has an invite in g1.</summary>
    private static readonly (GroupId, InviteId)[] BobsInvites = [(G1, I1)];

    private static readonly PendingInvite BobByAlice = new(G1, "Lisbon trip", "Bob", "Alice");

    /// <summary>The invites part of <see cref="Home"/>, which the invite scenarios are about.</summary>
    private static IReadOnlyList<PendingInvite> Read(
        Dictionary<GroupId, object[]> streams, UserId user, (GroupId, InviteId)[] invites, DateTimeOffset at) =>
        Home(streams, user, invites, at).Invites;

    /// <summary>
    /// Folds each group's history both ways the slice does — live, for invites; as the
    /// stored <see cref="Membership"/>, for groups (an empty history is no stream at all)
    /// — and reads.
    /// </summary>
    private static HomepageReadModel Home(
        Dictionary<GroupId, object[]> streams, UserId user, (GroupId, InviteId)[] invites, DateTimeOffset at)
    {
        var live = streams.ToDictionary(s => s.Key, s => s.Value.Length == 0 ? null : Fold.Of<State>(s.Value));
        var stored = streams
            .Where(s => s.Value.Length > 0)
            .Select(s => Fold.Of<Membership>(s.Value, ignoring: [typeof(MemberAdded), typeof(MemberInvited)])! with { Id = s.Key.Value })
            .ToList();
        return Reader.Read(new Query(user, invites, at), live, stored);
    }

    [Fact]
    public void S1_shows_a_live_invite() =>
        Assert.Equal([BobByAlice], Read(new() { [G1] = Lisbon }, Bob, BobsInvites, at: Day(1)));

    [Fact]
    public void S2_nobody_invited_this_address() =>
        Assert.Empty(Read(new() { [G1] = Lisbon }, Carol, [], at: Day(1)));

    [Fact]
    public void S3_an_invite_into_an_unknown_group_is_not_shown() =>
        Assert.Empty(Read(new() { [G1] = [] }, Bob, BobsInvites, at: Day(1)));

    [Fact]
    public void S4_an_invite_is_dead_at_its_deadline() =>
        Assert.Empty(Read(new() { [G1] = Lisbon }, Bob, BobsInvites, at: Day(30)));

    [Fact]
    public void S4_and_shown_just_before_it() =>
        Assert.Equal([BobByAlice], Read(new() { [G1] = Lisbon }, Bob, BobsInvites, at: Day(30).AddSeconds(-1)));

    [Fact]
    public void S5_only_the_slots_current_invite_counts() =>
        Assert.Empty(Read(new() { [G1] = [.. Lisbon, new MemberInvited(M2, I2, Day(31), Alice)] }, Bob, BobsInvites, at: Day(1)));

    [Fact]
    public void S6_a_claimed_slots_invite_is_used_up() =>
        Assert.Empty(Read(new() { [G1] = [.. Lisbon, new MemberClaimed(M2, Carol)] }, Bob, BobsInvites, at: Day(1)));

    [Fact]
    public void S7_an_invite_into_a_group_the_user_is_already_in_is_not_shown() =>
        Assert.Empty(Read(
            new() { [G1] = [.. Lisbon, new MemberAdded(M3, "Bobby", Alice), new MemberClaimed(M3, Bob)] },
            Bob, BobsInvites, at: Day(1)));

    [Fact]
    public void S8_the_inviter_is_named_by_their_slot_in_that_group() =>
        Assert.Equal(
            [new PendingInvite(G1, "Lisbon trip", "Bob", "Carol")],
            Read(
                new()
                {
                    [G1] =
                    [
                        .. Lisbon,
                        new MemberAdded(M3, "Carol", Alice),
                        new MemberClaimed(M3, Carol),
                        new MemberInvited(M2, I2, Day(30), Carol),
                    ],
                },
                Bob, [(G1, I2)], at: Day(1)));

    [Fact]
    public void S9_invites_from_several_groups_soonest_deadline_first() =>
        Assert.Equal(
            [new PendingInvite(G2, "Porto", "Bob", "Carol"), BobByAlice],
            Read(
                new()
                {
                    [G1] = Lisbon,
                    [G2] =
                    [
                        new GroupCreated(G2, "Porto", "EUR", Carol),
                        new MemberAdded(M4, "Carol", Carol),
                        new MemberClaimed(M4, Carol),
                        new MemberAdded(M5, "Bob", Carol),
                        new MemberInvited(M5, I2, Day(20), Carol),
                    ],
                },
                Bob, [(G1, I1), (G2, I2)], at: Day(1)));

    [Fact]
    public void An_invite_id_from_another_slot_names_nothing_here() =>
        Assert.Empty(Read(new() { [G1] = Lisbon }, Bob, [(G1, I2)], at: Day(1)));

    // ── groups ────────────────────────────────────────────────────────────────────

    private static readonly GroupId G3 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a3"));
    private static readonly UserId Dave = new(Guid.Parse("00000000-0000-0000-0000-0000000000c4"));

    private static object[] Group(GroupId id, string name, UserId creator, MemberId slot) =>
        [new GroupCreated(id, name, "GBP", creator), new MemberAdded(slot, "Creator", creator), new MemberClaimed(slot, creator)];

    [Fact]
    public void G1_a_brand_new_user_has_no_groups_and_no_invites()
    {
        var home = Home(new() { [G1] = Lisbon }, Dave, [], at: Day(1));

        Assert.Empty(home.Groups);
        Assert.Empty(home.Invites);
    }

    [Fact]
    public void G2_the_creators_group_is_listed() =>
        Assert.Equal([new GroupSummary(G1, "Lisbon trip")], Home(new() { [G1] = Lisbon }, Alice, [], at: Day(1)).Groups);

    [Fact]
    public void G3_a_joined_group_is_listed() =>
        Assert.Equal(
            [new GroupSummary(G1, "Lisbon trip")],
            Home(new() { [G1] = [.. Lisbon, new MemberClaimed(M2, Bob)] }, Bob, [], at: Day(1)).Groups);

    [Fact]
    public void G4_a_slot_added_for_you_or_invited_is_not_membership()
    {
        var home = Home(new() { [G1] = Lisbon }, Bob, BobsInvites, at: Day(1));

        Assert.Empty(home.Groups);
        Assert.Equal([BobByAlice], home.Invites);
    }

    [Fact]
    public void G5_several_groups_by_name() =>
        Assert.Equal(
            [new GroupSummary(G2, "Barcelona"), new GroupSummary(G1, "Lisbon trip"), new GroupSummary(G3, "Porto")],
            Home(
                new()
                {
                    [G1] = Lisbon,
                    [G2] = [.. Group(G2, "Barcelona", Carol, M4), new MemberAdded(M5, "Alice", Carol), new MemberClaimed(M5, Alice)],
                    [G3] = Group(G3, "Porto", Alice, M3),
                },
                Alice, [], at: Day(1)).Groups);

    [Fact]
    public void G6_other_peoples_groups_are_not_listed() =>
        Assert.Empty(Home(new() { [G1] = Lisbon }, Carol, [], at: Day(1)).Groups);

    [Fact]
    public void Names_sort_case_insensitively() =>
        Assert.Equal(
            ["alps", "Lisbon trip"],
            Home(new() { [G1] = Lisbon, [G2] = Group(G2, "alps", Alice, M4) }, Alice, [], at: Day(1)).Groups.Select(g => g.Name));
}
