using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using ShareExpenses.Shared;
using ShareExpenses.Slices.CreateGroup;
using ShareExpenses.Tests.Infrastructure;

namespace ShareExpenses.Tests.Slices.CreateGroup;

/// <summary>
/// CreateGroup runs on Wolverine (spec §12): the save happens in generated code, so
/// everything here goes through HTTP. Ids are generated per request, so specs read
/// them back from the response rather than pinning them.
/// </summary>
[Collection(AppCollection.Name)]
public class CreateGroupIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    // ── Specs against the real store ──────────────────────────────────────────────

    [Fact]
    public async Task S1_creates_the_group_and_its_first_member()
    {
        var response = await Create("Lisbon trip", "GBP", "Alice");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatedBody>())!;
        Assert.Equal(
            [
                new GroupCreated(body.GroupId, "Lisbon trip", "GBP", _alice),
                new MemberAdded(body.MemberId, "Alice", _alice),
                new MemberClaimed(body.MemberId, _alice),
            ],
            await StreamOf(body.GroupId));
    }

    [Fact]
    public async Task S4_a_group_is_created_exactly_once()
    {
        // Just before the save, another write starts a stream with the very same id.
        Guid? taken = null;
        app.BeforeNextSave.Arm(async pending =>
        {
            taken = Assert.Single(pending.PendingChanges.Streams()).Id;
            await using var other = Store.LightweightSession();
            other.Events.StartStream(taken.Value, new GroupCreated(GroupId.From(taken.Value), "Porto", "EUR", _alice));
            await other.SaveChangesAsync();
        });

        var response = await Create("Lisbon trip", "GBP", "Alice");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("group already exists", await response.Content.ReadAsStringAsync());
        var groupId = GroupId.From(taken!.Value);
        Assert.Equal([new GroupCreated(groupId, "Porto", "EUR", _alice)], await StreamOf(groupId));
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_creates_the_group_with_201_and_a_location()
    {
        var response = await Create("Lisbon trip", "gbp", "Alice");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreatedBody>())!;
        Assert.Equal($"/api/groups/{body.GroupId}", response.Headers.Location?.OriginalString);
        Assert.Equal("GBP", Assert.IsType<GroupCreated>((await StreamOf(body.GroupId))[0]).Currency);
    }

    [Fact]
    public async Task Post_is_stored_under_stable_event_type_names()
    {
        var response = await Create("Porto", "EUR", "Alice");
        var body = await response.Content.ReadFromJsonAsync<CreatedBody>();

        await using var session = Store.QuerySession();
        var names = (await session.Events.FetchStreamAsync(body!.GroupId.Value)).Select(e => e.EventTypeName);
        Assert.Equal(["group_created", "member_added", "member_claimed"], names);
    }

    [Fact]
    public async Task Post_rejects_an_invalid_command_with_400_and_the_reason()
    {
        var response = await Create("Lisbon trip", "XYZ", "Alice");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("currency must be a known ISO 4217 code", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_requires_a_signed_in_user()
    {
        var response = await app.CreateClient().PostAsJsonAsync("/api/groups",
            new { groupName = "Lisbon trip", currency = "GBP", memberName = "Alice" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> Create(string groupName, string currency, string memberName) =>
        app.ClientFor(_alice).PostAsJsonAsync("/api/groups", new { groupName, currency, memberName });

    private async Task<IReadOnlyList<object>> StreamOf(GroupId groupId)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(groupId.Value)).Select(e => e.Data).ToList();
    }

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);
}
