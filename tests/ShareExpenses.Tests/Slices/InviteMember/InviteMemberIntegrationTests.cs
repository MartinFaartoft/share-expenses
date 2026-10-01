using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Infrastructure.Marten;
using ShareExpenses.Shared;
using ShareExpenses.Slices.InviteMember;
using ShareExpenses.Tests.Infrastructure;
using ShareExpenses.Tests.Specs;
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
    private static readonly string H1 = InviteToken.Hash("t1");
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    private object[] Lisbon =>
    [
        new GroupCreated(_g1, "Lisbon trip", "GBP", _alice),
        new MemberAdded(_m1, "Alice", _alice),
        new MemberClaimed(_m1, _alice),
        new MemberAdded(_m2, "Bob", _alice),
    ];

    /// <summary>Real store, no accounts: the directory knows nobody.</summary>
    private StreamSpec<Command> Spec => new(Store, _g1.Value, async (session, command) =>
        await Handler.Handle(session, new NoAccounts(), command, default) switch
        {
            Outcome.Invited => null,
            Outcome.Invalid i => i.Reason,
            Outcome.NotFound n => n.Reason,
            var other => throw new InvalidOperationException($"Unhandled outcome {other}"),
        });

    // ── Specs against the real store: Marten's fold must agree with the test fold ──

    [Fact]
    public Task S1_invites_a_placeholder_member() =>
        Spec.Given(Lisbon)
            .When(new Command(_g1, _m2, "bob@example.com", H1, T0, _alice))
            .Then(new MemberInvited(_m2, H1, T0.AddDays(30), _alice));

    [Fact]
    public Task S2_the_group_must_exist() =>
        Spec.Given()
            .When(new Command(_g1, _m2, "bob@example.com", H1, T0, _alice))
            .ThenRejected("group not found");

    // ── Lookups for real: InviteDelivery and Identity ─────────────────────────────

    [Fact]
    public async Task S8_an_address_with_an_open_invite_cannot_be_invited_to_another_slot()
    {
        var (groupId, bob) = await GroupWithPlaceholder("Bob");
        var bobby = await AddPlaceholder(groupId, "Bobby");
        Assert.Equal(HttpStatusCode.OK, (await Invite(_alice, groupId, bob, "bob@example.com")).StatusCode);

        var response = await Invite(_alice, groupId, bobby, " BOB@example.com ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("that email is already invited as Bob", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task S10_the_creators_own_address_cannot_be_invited()
    {
        var aliceEmail = $"alice-{Guid.NewGuid():N}@example.com";
        await CreateAccount(_alice, aliceEmail);
        var (groupId, bob) = await GroupWithPlaceholder("Bob");

        var response = await Invite(_alice, groupId, bob, aliceEmail.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("has already joined as Alice", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Re_inviting_overwrites_the_delivery_keyed_to_the_latest_invite()
    {
        var (groupId, bob) = await GroupWithPlaceholder("Bob");
        await Invite(_alice, groupId, bob, "bob@old.example.com");

        Assert.Equal(HttpStatusCode.OK, (await Invite(_alice, groupId, bob, "bob@new.example.com")).StatusCode);
        var latest = await Invite(_alice, groupId, bob, "bob@new.example.com");
        Assert.Equal(HttpStatusCode.OK, latest.StatusCode);
        var link = await LinkFrom(latest);

        await using var session = Store.QuerySession();
        var delivery = Assert.Single(await session.Query<InviteDelivery>().Where(d => d.GroupId == groupId).ToListAsync());
        Assert.Equal((bob.Value, "bob@new.example.com"), (delivery.Id, delivery.Email));

        // The delivery is keyed to the latest invite: its link, and its event.
        Assert.Equal(InviteToken.Hash(TokenOf(link)), delivery.TokenHash);
        Assert.Equal(delivery.TokenHash, Assert.IsType<MemberInvited>((await StreamOf(groupId)).Last()).TokenHash);
    }

    // ── The ledger holds no address ───────────────────────────────────────────────

    [Fact]
    public async Task The_address_goes_to_the_delivery_document_and_never_into_the_ledger()
    {
        var (groupId, bob) = await GroupWithPlaceholder("Bob");

        var link = await LinkFrom(await Invite(_alice, groupId, bob, "  Bob@Example.com "));
        var token = TokenOf(link);

        await using var session = Store.QuerySession();
        var delivery = await session.LoadAsync<InviteDelivery>(bob.Value);
        Assert.Equal((groupId, "Bob@Example.com"), (delivery!.GroupId, delivery.Email));

        var events = await session.QueryAsync<string>(
            $"select data::text from {MartenSetup.Schema}.mt_events where stream_id = ?", groupId.Value);
        Assert.DoesNotContain(events, e => e.Contains('@'));
        var invited = Assert.Single(events, e => e.Contains("TokenHash"));
        Assert.DoesNotContain(token, invited);
        Assert.Contains(InviteToken.Hash(token), invited);
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_returns_200_with_a_group_scoped_link_and_emails_the_same_link()
    {
        var (groupId, bob) = await GroupWithPlaceholder("Bob");

        var response = await Invite(_alice, groupId, bob, " Bob@Example.com ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var link = await LinkFrom(response);
        // The token rides in the fragment, so no server ever receives it in a URL.
        Assert.Matches($"^http://localhost/invites/{groupId}#[A-Za-z0-9_-]{{43}}$", link);
        Assert.Equal(
            new SentInvite("Bob@Example.com", link, "Lisbon trip", "Alice", "Bob"),
            Assert.Single(app.Emails.Invites, e => e.Link.Contains(groupId.ToString())));
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

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(await LinkFrom(response));
            Assert.IsType<MemberInvited>((await StreamOf(groupId)).Last());
        }
        finally
        {
            app.Emails.FailInvitesTo = _ => false;
        }
    }

    [Fact]
    public async Task Post_rejects_an_invalid_email_with_400_and_writes_nothing()
    {
        var (groupId, bob) = await GroupWithPlaceholder("Bob");

        var response = await Invite(_alice, groupId, bob, "bob");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("email is not a valid address", await response.Content.ReadAsStringAsync());
        await using var session = Store.QuerySession();
        Assert.Null(await session.LoadAsync<InviteDelivery>(bob.Value));
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

    private async Task<(GroupId Group, MemberId Placeholder)> GroupWithPlaceholder(string name)
    {
        var created = await app.ClientFor(_alice).PostAsJsonAsync("/api/groups",
            new { groupName = "Lisbon trip", currency = "GBP", memberName = "Alice" });
        var groupId = (await created.Content.ReadFromJsonAsync<CreatedBody>())!.GroupId;
        return (groupId, await AddPlaceholder(groupId, name));
    }

    private async Task<MemberId> AddPlaceholder(GroupId groupId, string name)
    {
        var added = await app.ClientFor(_alice).PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = name });
        return (await added.Content.ReadFromJsonAsync<AddedBody>())!.MemberId;
    }

    private async Task CreateAccount(UserId id, string email)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var result = await users.CreateAsync(new User { Id = id.Value, UserName = email, Email = email });
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
    }

    private Task<HttpResponseMessage> Invite(UserId actor, GroupId groupId, MemberId memberId, string email) =>
        app.ClientFor(actor).PostAsJsonAsync($"/api/groups/{groupId}/members/{memberId}/invite", new { email });

    private static async Task<string> LinkFrom(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<InvitedBody>())!.Link;

    private static string TokenOf(string link) => link[(link.IndexOf('#') + 1)..];

    private async Task<IReadOnlyList<object>> StreamOf(GroupId groupId)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(groupId.Value)).Select(e => e.Data).ToList();
    }

    private sealed class NoAccounts : IEmailDirectory
    {
        public Task<UserId?> AccountFor(string email, CancellationToken ct = default) => Task.FromResult<UserId?>(null);
    }

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);

    private sealed record InvitedBody(string Link);
}
