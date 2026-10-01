using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly MemberId _m3 = MemberId.New();
    private readonly UserId _alice = UserId.New();
    private readonly UserId _mallory = UserId.New();
    private static readonly string H0 = InviteToken.Hash("t0");
    private static readonly string H1 = InviteToken.Hash("t1");

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    private object[] Lisbon =>
    [
        new GroupCreated(_g1, "Lisbon trip", "GBP", _alice),
        new MemberAdded(_m1, "Alice", _alice),
        new MemberClaimed(_m1, _alice),
        new MemberAdded(_m2, "Bob", _alice),
    ];

    private StreamSpec<Command> Spec => new(Store, _g1.Value, async (session, command) =>
        await Handler.Handle(session, command, default) switch
        {
            Outcome.Invited => null,
            Outcome.Invalid i => i.Reason,
            Outcome.GroupNotFound => Decider.GroupNotFound,
            Outcome.MemberNotFound => Decider.MemberNotFound,
            var other => throw new InvalidOperationException($"Unhandled outcome {other}"),
        });

    // ── Specs against the real store: Marten's fold must agree with the test fold ──

    [Fact]
    public Task S1_invites_a_placeholder_member() =>
        Spec.Given(Lisbon)
            .When(new Command(_g1, _m2, "bob@example.com", H1, _alice))
            .Then(new MemberInvited(_m2, "bob@example.com", H1, _alice));

    [Fact]
    public Task S3_the_group_must_exist() =>
        Spec.Given()
            .When(new Command(_g1, _m2, "bob@example.com", H1, _alice))
            .ThenRejected("group not found");

    [Fact]
    public Task S9_an_email_cannot_be_invited_to_two_slots() =>
        Spec.Given([.. Lisbon, new MemberAdded(_m3, "Bobby", _alice), new MemberInvited(_m2, "bob@example.com", H0, _alice)])
            .When(new Command(_g1, _m3, "BOB@example.com", H1, _alice))
            .ThenRejected("that email is already invited as Bob");

    // ── HTTP ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_returns_200_with_a_group_scoped_link_and_emails_the_same_link()
    {
        var (groupId, bob) = await GroupWithPlaceholderBob();

        var response = await Invite(_alice, groupId, bob, "Bob@Example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var link = (await response.Content.ReadFromJsonAsync<InvitedBody>())!.Link;
        Assert.Matches($"^http://localhost/invites/{groupId}/[A-Za-z0-9_-]{{43}}$", link);
        Assert.Equal(
            new SentInvite("Bob@Example.com", link, "Lisbon trip", "Alice", "Bob"),
            Assert.Single(app.Emails.Invites, e => e.Link.Contains(groupId.ToString())));
    }

    [Fact]
    public async Task The_stored_event_holds_the_tokens_hash_and_never_the_token()
    {
        var (groupId, bob) = await GroupWithPlaceholderBob();

        var link = (await (await Invite(_alice, groupId, bob, "bob@example.com")).Content.ReadFromJsonAsync<InvitedBody>())!.Link;
        var token = link[(link.LastIndexOf('/') + 1)..];

        await using var session = Store.QuerySession();
        var stored = Assert.Single(await session.QueryAsync<string>(
            $"select data::text from {MartenSetup.Schema}.mt_events where stream_id = ? and type = 'member_invited'",
            groupId.Value));
        Assert.DoesNotContain(token, stored);
        Assert.Contains(InviteToken.Hash(token), stored);
    }

    [Fact]
    public async Task Post_still_succeeds_when_the_email_cannot_be_sent()
    {
        var (groupId, bob) = await GroupWithPlaceholderBob();
        var address = $"{Guid.NewGuid():N}@undeliverable.example";
        app.Emails.FailInvitesTo = email => email == address;
        try
        {
            var response = await Invite(_alice, groupId, bob, address);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull((await response.Content.ReadFromJsonAsync<InvitedBody>())!.Link);
            Assert.IsType<MemberInvited>((await StreamOf(groupId)).Last());
        }
        finally
        {
            app.Emails.FailInvitesTo = _ => false;
        }
    }

    [Fact]
    public async Task Post_rejects_an_invalid_email_with_400_and_the_reason()
    {
        var (groupId, bob) = await GroupWithPlaceholderBob();

        var response = await Invite(_alice, groupId, bob, "bob");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("email is not a valid address", await response.Content.ReadAsStringAsync());
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
        var (groupId, _) = await GroupWithPlaceholderBob();

        var response = await app.ClientFor(_alice).PostAsJsonAsync(
            $"/api/groups/{groupId}/members/{memberId}/invite", new { email = "bob@example.com" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("member not found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_answers_a_non_member_exactly_as_for_a_missing_group_even_with_a_malformed_slot()
    {
        var (groupId, _) = await GroupWithPlaceholderBob();
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

    private async Task<(GroupId Group, MemberId Bob)> GroupWithPlaceholderBob()
    {
        var client = app.ClientFor(_alice);
        var created = await client.PostAsJsonAsync("/api/groups",
            new { groupName = "Lisbon trip", currency = "GBP", memberName = "Alice" });
        var groupId = (await created.Content.ReadFromJsonAsync<CreatedBody>())!.GroupId;
        var added = await client.PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = "Bob" });
        return (groupId, (await added.Content.ReadFromJsonAsync<AddedBody>())!.MemberId);
    }

    private Task<HttpResponseMessage> Invite(UserId as_, GroupId groupId, MemberId memberId, string email) =>
        app.ClientFor(as_).PostAsJsonAsync($"/api/groups/{groupId}/members/{memberId}/invite", new { email });

    private async Task<IReadOnlyList<object>> StreamOf(GroupId groupId)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(groupId.Value)).Select(e => e.Data).ToList();
    }

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);

    private sealed record InvitedBody(string Link);
}
