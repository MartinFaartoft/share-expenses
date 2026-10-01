using System.Net;
using System.Net.Http.Json;
using Marten;
using Marten.Services;
using Microsoft.Extensions.DependencyInjection;
using ShareExpenses.Shared;
using ShareExpenses.Slices.AddMember;
using ShareExpenses.Tests.Infrastructure;
using ShareExpenses.Tests.Specs;
// Only the public events: tests can see every slice's internals, so a namespace
// import would bring in CreateGroup's own Command too.
using GroupCreated = ShareExpenses.Slices.CreateGroup.GroupCreated;
using MemberAdded = ShareExpenses.Slices.CreateGroup.MemberAdded;
using MemberClaimed = ShareExpenses.Slices.CreateGroup.MemberClaimed;

namespace ShareExpenses.Tests.Slices.AddMember;

[Collection(AppCollection.Name)]
public class AddMemberIntegrationTests(AppFixture app)
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
    ];

    private StreamSpec<Command> Spec => new(Store, _g1.Value, async (session, command) =>
        await Handler.Handle(session, command, _m2, default) switch
        {
            Outcome.Added => null,
            Outcome.Invalid i => i.Reason,
            Outcome.NotFound => Decider.GroupNotFound,
            var other => throw new InvalidOperationException($"Unhandled outcome {other}"),
        });

    // ── Specs against the real store: Marten's fold must agree with the test fold ──

    [Fact]
    public Task S1_adds_a_placeholder_member_by_name_alone() =>
        Spec.Given(Lisbon)
            .When(new Command(_g1, "Bob", _alice))
            .Then(new MemberAdded(_m2, "Bob", _alice));

    [Fact]
    public Task S3_the_group_must_exist() =>
        Spec.Given()
            .When(new Command(_g1, "Bob", _alice))
            .ThenRejected("group not found");

    [Fact]
    public Task S4_only_members_of_the_group_may_add() =>
        Spec.Given(Lisbon)
            .When(new Command(_g1, "Bob", _mallory))
            .ThenRejected("group not found");

    [Fact]
    public Task S7_rejects_a_name_already_used_in_the_group() =>
        Spec.Given(Lisbon)
            .When(new Command(_g1, " alice ", _alice))
            .ThenRejected("a member with that name already exists");

    // ── Concurrency: two phones at the same table ─────────────────────────────────

    [Fact]
    public async Task Two_phones_at_once_the_second_save_conflicts_and_appends_nothing()
    {
        await StartLisbon();
        var carol = MemberId.New();

        // Phone A fetches and decides; just before it saves, phone B adds Carol.
        var phoneB = new BeforeSave(async () =>
        {
            await using var b = Store.LightweightSession();
            Assert.IsType<Outcome.Added>(await Handler.Handle(b, new Command(_g1, "Carol", _alice), carol, default));
        });
        await using var a = Store.LightweightSession(new SessionOptions { Listeners = { phoneB } });

        var outcome = await Handler.Handle(a, new Command(_g1, "Bob", _alice), _m2, default);

        Assert.IsType<Outcome.Conflict>(outcome);
        Assert.Equal([.. Lisbon, new MemberAdded(carol, "Carol", _alice)], await StreamOf(_g1));
    }

    [Fact]
    public async Task After_a_conflict_the_retry_decides_against_the_new_state()
    {
        await StartLisbon();

        // Both phones add "Bob"; phone B's lands first.
        var phoneB = new BeforeSave(async () =>
        {
            await using var b = Store.LightweightSession();
            await Handler.Handle(b, new Command(_g1, "Bob", _alice), MemberId.New(), default);
        });
        await using (var a = Store.LightweightSession(new SessionOptions { Listeners = { phoneB } }))
            Assert.IsType<Outcome.Conflict>(await Handler.Handle(a, new Command(_g1, "Bob", _alice), _m2, default));

        // Phone A retries, as the client is expected to (409 → resend).
        await using var retry = Store.LightweightSession();
        Assert.Equal(
            new Outcome.Invalid("a member with that name already exists"),
            await Handler.Handle(retry, new Command(_g1, "Bob", _alice), _m2, default));
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_adds_the_member_with_201_and_a_location()
    {
        var groupId = await CreateGroupOverHttp();

        var response = await app.ClientFor(_alice).PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = "Bob" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AddedBody>();
        Assert.Equal($"/api/groups/{groupId}/members/{body!.MemberId}", response.Headers.Location?.OriginalString);
        Assert.Equal(new MemberAdded(body.MemberId, "Bob", _alice), (await StreamOf(groupId)).Last());
    }

    [Fact]
    public async Task Post_rejects_an_invalid_name_with_400_and_the_reason()
    {
        var groupId = await CreateGroupOverHttp();

        var response = await app.ClientFor(_alice).PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = "alice" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("a member with that name already exists", await response.Content.ReadAsStringAsync());
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
        var client = app.ClientFor(_mallory);

        var existing = await client.PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = "Bob" });
        var missing = await client.PostAsJsonAsync($"/api/groups/{GroupId.New()}/members", new { displayName = "Bob" });

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

    private async Task StartLisbon()
    {
        await using var session = Store.LightweightSession();
        session.Events.StartStream(_g1.Value, Lisbon);
        await session.SaveChangesAsync();
    }

    private async Task<GroupId> CreateGroupOverHttp()
    {
        var response = await app.ClientFor(_alice).PostAsJsonAsync("/api/groups",
            new { name = "Lisbon trip", currency = "GBP", displayName = "Alice" });
        return (await response.Content.ReadFromJsonAsync<CreatedBody>())!.GroupId;
    }

    private async Task<IReadOnlyList<object>> StreamOf(GroupId groupId)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(groupId.Value)).Select(e => e.Data).ToList();
    }

    /// <summary>Runs <paramref name="competitor"/> just before the session saves: a second phone, on cue.</summary>
    private sealed class BeforeSave(Func<Task> competitor) : DocumentSessionListenerBase
    {
        public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token) => competitor();
    }

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);
}
