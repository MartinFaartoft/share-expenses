using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using ShareExpenses.Shared;
using ShareExpenses.Tests.Infrastructure;
// Only the public events: tests can see every slice's internals, so a namespace
// import would bring in CreateGroup's own Command too.
using GroupCreated = ShareExpenses.Slices.CreateGroup.GroupCreated;
using MemberAdded = ShareExpenses.Slices.CreateGroup.MemberAdded;
using MemberClaimed = ShareExpenses.Slices.CreateGroup.MemberClaimed;

namespace ShareExpenses.Tests.Slices.AddMember;

/// <summary>
/// AddMember runs on Wolverine (spec §12): fetch, append and save happen in
/// generated code with no handler of ours to call, so everything below goes through
/// HTTP. The member id is generated per request, so specs read it back from the
/// response rather than pinning it.
/// </summary>
[Collection(AppCollection.Name)]
public class AddMemberIntegrationTests(AppFixture app)
{
    // Fresh ids per test: the container is shared by every test in the run.
    private readonly GroupId _g1 = GroupId.New();
    private readonly MemberId _m1 = MemberId.New();
    private readonly UserId _alice = UserId.New();
    private readonly UserId _mallory = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    private object[] Lisbon =>
    [
        new GroupCreated(_g1, "Lisbon trip", "GBP", _alice),
        new MemberAdded(_m1, "Alice", _alice),
        new MemberClaimed(_m1, _alice),
    ];

    // ── Specs against the real store: Marten's fold must agree with the test fold ──

    [Fact]
    public async Task S1_adds_a_placeholder_member_by_name_alone()
    {
        await Given(Lisbon);

        var response = await AddMember(_g1, "Bob", _alice);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var added = await response.Content.ReadFromJsonAsync<AddedBody>();
        Assert.Equal([.. Lisbon, new MemberAdded(added!.MemberId, "Bob", _alice)], await StreamOf(_g1));
    }

    [Fact]
    public async Task S3_the_group_must_exist()
    {
        var response = await AddMember(_g1, "Bob", _alice);

        await AssertRejected(response, HttpStatusCode.NotFound, "group not found");
        Assert.Empty(await StreamOf(_g1));
    }

    [Fact]
    public async Task S4_only_members_of_the_group_may_add()
    {
        await Given(Lisbon);

        var response = await AddMember(_g1, "Bob", _mallory);

        await AssertRejected(response, HttpStatusCode.NotFound, "group not found");
        Assert.Equal(Lisbon, await StreamOf(_g1));
    }

    [Fact]
    public async Task S7_rejects_a_name_already_used_in_the_group()
    {
        await Given(Lisbon);

        var response = await AddMember(_g1, " alice ", _alice);

        await AssertRejected(response, HttpStatusCode.BadRequest, "a member with that name already exists");
        Assert.Equal(Lisbon, await StreamOf(_g1));
    }

    // ── Concurrency: two phones at the same table ─────────────────────────────────

    [Fact]
    public async Task Two_phones_at_once_the_second_save_conflicts_and_appends_nothing()
    {
        await Given(Lisbon);
        MemberId? carol = null;

        // Phone A fetches and decides; just before it saves, phone B adds Carol.
        app.BeforeNextSave.Arm(async () =>
        {
            var b = await AddMember(_g1, "Carol", _alice);
            Assert.Equal(HttpStatusCode.Created, b.StatusCode);
            carol = (await b.Content.ReadFromJsonAsync<AddedBody>())!.MemberId;
        });

        var a = await AddMember(_g1, "Bob", _alice);

        Assert.Equal(HttpStatusCode.Conflict, a.StatusCode);
        Assert.Contains("the group changed while you were adding", await a.Content.ReadAsStringAsync());
        Assert.Equal([.. Lisbon, new MemberAdded(carol!.Value, "Carol", _alice)], await StreamOf(_g1));
    }

    [Fact]
    public async Task After_a_conflict_the_retry_decides_against_the_new_state()
    {
        await Given(Lisbon);

        // Both phones add "Bob"; phone B's lands first.
        app.BeforeNextSave.Arm(async () => await AddMember(_g1, "Bob", _alice));
        Assert.Equal(HttpStatusCode.Conflict, (await AddMember(_g1, "Bob", _alice)).StatusCode);

        // Phone A retries, as the client is expected to (409 → resend).
        var retry = await AddMember(_g1, "Bob", _alice);

        await AssertRejected(retry, HttpStatusCode.BadRequest, "a member with that name already exists");
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_adds_the_member_with_201_and_a_location()
    {
        var groupId = await CreateGroupOverHttp();

        var response = await AddMember(groupId, "Bob", _alice);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AddedBody>();
        Assert.Equal($"/api/groups/{groupId}/members/{body!.MemberId}", response.Headers.Location?.OriginalString);
        Assert.Equal(new MemberAdded(body.MemberId, "Bob", _alice), (await StreamOf(groupId)).Last());
    }

    [Fact]
    public async Task Post_rejects_an_invalid_name_with_400_and_the_reason()
    {
        var groupId = await CreateGroupOverHttp();

        var response = await AddMember(groupId, "alice", _alice);

        await AssertRejected(response, HttpStatusCode.BadRequest, "a member with that name already exists");
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("01a0f734-90c1-761a-87e3-07c09ef9be9f")] // well-formed, no such group
    public async Task Post_answers_404_for_a_malformed_or_unknown_group(string groupId)
    {
        var response = await app.ClientFor(_alice).PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = "Bob" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_answers_a_non_member_exactly_as_for_a_missing_group()
    {
        var groupId = await CreateGroupOverHttp();

        var existing = await AddMember(groupId, "Bob", _mallory);
        var missing = await AddMember(GroupId.New(), "Bob", _mallory);

        Assert.Equal(HttpStatusCode.NotFound, existing.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await existing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_requires_a_signed_in_user()
    {
        var response = await app.CreateClient().PostAsJsonAsync($"/api/groups/{GroupId.New()}/members", new { displayName = "Bob" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private async Task Given(params object[] history)
    {
        await using var session = Store.LightweightSession();
        session.Events.StartStream(_g1.Value, history);
        await session.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> AddMember(GroupId groupId, string displayName, UserId by) =>
        app.ClientFor(by).PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName });

    private static async Task AssertRejected(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Contains(reason, await response.Content.ReadAsStringAsync());
    }

    private async Task<GroupId> CreateGroupOverHttp()
    {
        var response = await app.ClientFor(_alice).PostAsJsonAsync("/api/groups",
            new { groupName = "Lisbon trip", currency = "GBP", memberName = "Alice" });
        return (await response.Content.ReadFromJsonAsync<CreatedBody>())!.GroupId;
    }

    private async Task<IReadOnlyList<object>> StreamOf(GroupId groupId)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(groupId.Value)).Select(e => e.Data).ToList();
    }

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);
}
