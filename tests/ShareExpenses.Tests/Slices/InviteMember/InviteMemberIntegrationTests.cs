using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using ShareExpenses.Infrastructure.Invites;
using ShareExpenses.Infrastructure.Marten;
using ShareExpenses.Shared;
using ShareExpenses.Slices.InviteMember;
using ShareExpenses.Tests.Infrastructure;
// Only the public events: tests can see every slice's internals, so a namespace
// import would bring in CreateGroup's own Command too.
using GroupCreated = ShareExpenses.Slices.CreateGroup.GroupCreated;
using MemberAdded = ShareExpenses.Slices.CreateGroup.MemberAdded;
using MemberClaimed = ShareExpenses.Slices.CreateGroup.MemberClaimed;

namespace ShareExpenses.Tests.Slices.InviteMember;

[Collection(AppCollection.Name)]
public class InviteMemberIntegrationTests(AppFixture app)
{
    // Fresh ids per test: the container is shared by every test in the run.
    private readonly GroupId _g1 = GroupId.New();
    private readonly MemberId _m1 = MemberId.New();
    private readonly MemberId _m2 = MemberId.New();
    private readonly UserId _alice = UserId.New();
    private readonly UserId _mallory = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    private object[] Lisbon =>
    [
        new GroupCreated(_g1, "Lisbon trip", "GBP", _alice),
        new MemberAdded(_m1, "Alice", _alice),
        new MemberClaimed(_m1, _alice),
        new MemberAdded(_m2, "Bob", _alice),
    ];

    // ── Specs against the real store: Marten's fold must agree with the test fold ──

    [Fact]
    public async Task S1_invites_a_placeholder_member()
    {
        await Given(Lisbon);
        var before = app.Clock.GetUtcNow();

        var response = await Invite(_alice, _g1, _m2, "bob@example.com");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stream = await StreamOf(_g1);
        Assert.Equal(Lisbon, stream.Take(Lisbon.Length));
        var invited = Assert.IsType<MemberInvited>(Assert.Single(stream.Skip(Lisbon.Length)));
        Assert.Equal((_m2, _alice), (invited.MemberId, invited.By));
        Assert.InRange(invited.ExpiresAt, before.AddDays(30), app.Clock.GetUtcNow().AddDays(30));

        // The event names the Invite document that holds the address.
        var invite = await InviteFor(_g1, _m2);
        Assert.Equal((invited.InviteId, "bob@example.com"), (invite!.InviteId, invite.Email));
    }

    [Fact]
    public async Task S2_the_group_must_exist()
    {
        var response = await Invite(_alice, _g1, _m2, "bob@example.com");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("group not found", await response.Content.ReadAsStringAsync());
        Assert.Empty(await StreamOf(_g1));
    }

    // ── Lookups for real: Invite documents and Identity ──────────────────────────

    [Fact]
    public async Task S8_an_address_with_an_open_invite_cannot_be_invited_to_another_slot()
    {
        var (groupId, bob) = await GroupWithPlaceholder("Bob");
        var bobby = await AddPlaceholder(groupId, "Bobby");
        Assert.Equal(HttpStatusCode.NoContent, (await Invite(_alice, groupId, bob, "bob@example.com")).StatusCode);

        var response = await Invite(_alice, groupId, bobby, " BOB@example.com ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("that email is already invited as Bob", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task S10_the_creators_own_address_cannot_be_invited()
    {
        var aliceEmail = $"alice-{Guid.NewGuid():N}@example.com";
        var alice = await app.AccountFor(aliceEmail);
        var (groupId, bob) = await GroupWithPlaceholder("Bob", alice);

        var response = await Invite(alice, groupId, bob, aliceEmail.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("has already joined as Alice", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Re_inviting_replaces_the_slots_invite_with_one_the_latest_event_names()
    {
        var (groupId, bob) = await GroupWithPlaceholder("Bob");
        await Invite(_alice, groupId, bob, "bob@old.example.com");

        Assert.Equal(HttpStatusCode.NoContent, (await Invite(_alice, groupId, bob, "bob@new.example.com")).StatusCode);

        await using var session = Store.QuerySession();
        var invite = Assert.Single(await session.Query<Invite>().Where(i => i.GroupId == groupId).ToListAsync());
        Assert.Equal((bob, "bob@new.example.com"), (invite.MemberId, invite.Email));
        Assert.Equal(invite.InviteId, Assert.IsType<MemberInvited>((await StreamOf(groupId)).Last()).InviteId);
    }

    // ── The ledger holds no address ───────────────────────────────────────────────

    [Fact]
    public async Task The_address_goes_to_the_invite_document_and_never_into_the_ledger()
    {
        var (groupId, bob) = await GroupWithPlaceholder("Bob");

        await Invite(_alice, groupId, bob, "  Bob@Example.com ");

        var invite = await InviteFor(groupId, bob);
        Assert.Equal(("Bob@Example.com", "BOB@EXAMPLE.COM"), (invite!.Email, invite.NormalizedEmail));

        await using var session = Store.QuerySession();
        var events = await session.QueryAsync<string>(
            $"select data::text from {MartenSetup.Schema}.mt_events where stream_id = ?", groupId.Value);
        Assert.DoesNotContain(events, e => e.Contains('@'));
    }

    // ── The email ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_returns_204_and_emails_the_invite_with_no_secret_in_it()
    {
        var (groupId, bob) = await GroupWithPlaceholder("Bob");
        var address = $"{Guid.NewGuid():N}@example.com";

        var response = await Invite(_alice, groupId, bob, address);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            new SentInvite(address, "http://localhost/", "Lisbon trip", "Alice", "Bob"),
            Assert.Single(app.Emails.Invites, e => e.Email == address));
    }

    [Fact]
    public async Task Post_still_succeeds_when_the_email_cannot_be_sent()
    {
        var (groupId, bob) = await GroupWithPlaceholder("Bob");
        var address = $"{Guid.NewGuid():N}@undeliverable.example";
        app.Emails.FailInvitesTo = email => email == address;
        try
        {
            var response = await Invite(_alice, groupId, bob, address);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.IsType<MemberInvited>((await StreamOf(groupId)).Last());
        }
        finally
        {
            app.Emails.FailInvitesTo = _ => false;
        }
    }

    // ── Concurrency ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_lost_race_is_409_writes_nothing_and_sends_no_email()
    {
        await Given(Lisbon);
        var address = $"{Guid.NewGuid():N}@example.com";

        // Just before the invite saves, another phone adds Carol.
        app.BeforeNextSave.Arm(async () =>
            Assert.Equal(HttpStatusCode.Created,
                (await app.ClientFor(_alice).PostAsJsonAsync($"/api/groups/{_g1}/members", new { displayName = "Carol" })).StatusCode));

        var response = await Invite(_alice, _g1, _m2, address);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(await StreamOf(_g1), e => e is MemberInvited);
        Assert.DoesNotContain(app.Emails.Invites, e => e.Email == address);
        Assert.Null(await InviteFor(_g1, _m2));
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_rejects_an_invalid_email_with_400_and_writes_nothing()
    {
        var (groupId, bob) = await GroupWithPlaceholder("Bob");

        var response = await Invite(_alice, groupId, bob, "bob");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("email is not a valid address", await response.Content.ReadAsStringAsync());
        Assert.Null(await InviteFor(groupId, bob));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("01a0f734-90c1-761a-87e3-07c09ef9be9f")] // well-formed, no such group
    public async Task Post_answers_404_group_not_found_for_a_malformed_or_unknown_group(string groupId)
    {
        var response = await app.ClientFor(_alice).PostAsJsonAsync(
            $"/api/groups/{groupId}/members/{MemberId.New()}/invite", new { email = "bob@example.com" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("group not found", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("01a0f734-90c1-761a-87e3-07c09ef9be9f")] // well-formed, no such slot
    public async Task Post_answers_a_member_404_member_not_found_for_a_malformed_or_unknown_slot(string memberId)
    {
        var (groupId, _) = await GroupWithPlaceholder("Bob");

        var response = await app.ClientFor(_alice).PostAsJsonAsync(
            $"/api/groups/{groupId}/members/{memberId}/invite", new { email = "bob@example.com" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("member not found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_answers_a_non_member_exactly_as_for_a_missing_group_even_with_a_malformed_slot()
    {
        var (groupId, _) = await GroupWithPlaceholder("Bob");
        var client = app.ClientFor(_mallory);

        var existing = await client.PostAsJsonAsync($"/api/groups/{groupId}/members/nope/invite", new { email = "x" });
        var missing = await client.PostAsJsonAsync($"/api/groups/{GroupId.New()}/members/nope/invite", new { email = "x" });

        Assert.Equal(HttpStatusCode.NotFound, existing.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await existing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_requires_a_signed_in_user()
    {
        var response = await app.CreateClient().PostAsJsonAsync(
            $"/api/groups/{GroupId.New()}/members/{MemberId.New()}/invite", new { email = "bob@example.com" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private async Task Given(params object[] history)
    {
        await using var session = Store.LightweightSession();
        session.Events.StartStream(_g1.Value, history);
        await session.SaveChangesAsync();
    }

    private async Task<(GroupId Group, MemberId Placeholder)> GroupWithPlaceholder(string name, UserId? creator = null)
    {
        var created = await app.ClientFor(creator ?? _alice).PostAsJsonAsync("/api/groups",
            new { groupName = "Lisbon trip", currency = "GBP", memberName = "Alice" });
        var groupId = (await created.Content.ReadFromJsonAsync<CreatedBody>())!.GroupId;
        return (groupId, await AddPlaceholder(groupId, name, creator));
    }

    private async Task<MemberId> AddPlaceholder(GroupId groupId, string name, UserId? actor = null)
    {
        var added = await app.ClientFor(actor ?? _alice).PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = name });
        return (await added.Content.ReadFromJsonAsync<AddedBody>())!.MemberId;
    }

    private Task<HttpResponseMessage> Invite(UserId actor, GroupId groupId, MemberId memberId, string email) =>
        app.ClientFor(actor).PostAsJsonAsync($"/api/groups/{groupId}/members/{memberId}/invite", new { email });

    private async Task<Invite?> InviteFor(GroupId groupId, MemberId memberId)
    {
        await using var session = Store.QuerySession();
        return await session.Query<Invite>().SingleOrDefaultAsync(i => i.GroupId == groupId && i.MemberId == memberId);
    }

    private async Task<IReadOnlyList<object>> StreamOf(GroupId groupId)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(groupId.Value)).Select(e => e.Data).ToList();
    }

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);
}
