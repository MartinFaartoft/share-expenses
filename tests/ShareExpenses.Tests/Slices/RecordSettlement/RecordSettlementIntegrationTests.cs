using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using ShareExpenses.Infrastructure.Marten;
using ShareExpenses.Shared;
using ShareExpenses.Slices.RecordSettlement;
using ShareExpenses.Tests.Infrastructure;

namespace ShareExpenses.Tests.Slices.RecordSettlement;

/// <summary>Recording settlements through HTTP, against the real store.</summary>
[Collection(AppCollection.Name)]
public class RecordSettlementIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();
    private readonly UserId _mallory = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    [Fact]
    public async Task S1_records_a_settlement_with_201_and_a_location()
    {
        var (groupId, alice, bob) = await Lisbon();

        var response = await Record(groupId, Payment(bob, alice, 3000));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var settlementId = (await response.Content.ReadFromJsonAsync<RecordedBody>())!.SettlementId;
        Assert.Equal($"/api/groups/{groupId}/settlements/{settlementId}", response.Headers.Location?.OriginalString);
        Assert.Equal(
            new SettlementRecorded(settlementId, bob, alice, 3000, new DateOnly(2026, 10, 1), _alice),
            (await StreamOf(groupId)).Last());
    }

    [Fact]
    public async Task Stored_under_a_stable_name()
    {
        var (groupId, alice, bob) = await Lisbon();

        await Record(groupId, Payment(bob, alice, 3000));

        await using var session = Store.QuerySession();
        var types = await session.QueryAsync<string>(
            $"select type from {MartenSetup.Schema}.mt_events where stream_id = ? order by version desc limit 1", groupId.Value);
        Assert.Equal("settlement_recorded", Assert.Single(types));
    }

    [Fact]
    public async Task Two_phones_at_once_the_second_save_conflicts_and_appends_nothing()
    {
        var (groupId, alice, bob) = await Lisbon();
        app.BeforeNextSave.Arm(async () =>
            Assert.Equal(HttpStatusCode.Created, (await Record(groupId, Payment(bob, alice, 3000))).StatusCode));

        var response = await Record(groupId, Payment(bob, alice, 3000));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single((await StreamOf(groupId)).OfType<SettlementRecorded>());
    }

    [Fact]
    public async Task Post_rejects_an_invalid_settlement_with_400_and_the_reason()
    {
        var (groupId, _, bob) = await Lisbon();

        var response = await Record(groupId, Payment(bob, bob, 3000));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("a member cannot pay themselves", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_answers_a_non_member_exactly_as_for_a_missing_or_malformed_group()
    {
        var (groupId, alice, bob) = await Lisbon();
        var client = app.ClientFor(_mallory);

        HttpResponseMessage[] responses =
        [
            await client.PostAsJsonAsync($"/api/groups/{groupId}/settlements", Payment(bob, alice, 3000)),
            await client.PostAsJsonAsync($"/api/groups/{GroupId.New()}/settlements", Payment(bob, alice, 3000)),
            await client.PostAsJsonAsync("/api/groups/not-a-guid/settlements", Payment(bob, alice, 3000)),
        ];

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Single((await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()))).Distinct());
    }

    [Fact]
    public async Task Post_requires_a_signed_in_user()
    {
        var response = await app.CreateClient().PostAsJsonAsync(
            $"/api/groups/{GroupId.New()}/settlements", Payment(MemberId.New(), MemberId.New(), 3000));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Alice creates "Lisbon trip" and adds Bob as a placeholder.</summary>
    private async Task<(GroupId Group, MemberId Alice, MemberId Bob)> Lisbon()
    {
        var client = app.ClientFor(_alice);
        var created = await (await client.PostAsJsonAsync("/api/groups",
            new { groupName = "Lisbon trip", currency = "GBP", memberName = "Alice" })).Content.ReadFromJsonAsync<CreatedBody>();
        var bob = (await (await client.PostAsJsonAsync($"/api/groups/{created!.GroupId}/members", new { displayName = "Bob" }))
            .Content.ReadFromJsonAsync<AddedBody>())!.MemberId;
        return (created.GroupId, created.MemberId, bob);
    }

    private static object Payment(MemberId from, MemberId to, long amount) =>
        new { fromMemberId = from, toMemberId = to, amountMinor = amount, paidOn = "2026-10-01" };

    private Task<HttpResponseMessage> Record(GroupId groupId, object body) =>
        app.ClientFor(_alice).PostAsJsonAsync($"/api/groups/{groupId}/settlements", body);

    private async Task<IReadOnlyList<object>> StreamOf(GroupId groupId)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(groupId.Value)).Select(e => e.Data).ToList();
    }

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);

    private sealed record RecordedBody(SettlementId SettlementId);
}
