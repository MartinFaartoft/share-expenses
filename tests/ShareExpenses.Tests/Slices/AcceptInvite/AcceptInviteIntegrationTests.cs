using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using ShareExpenses.Infrastructure.Invites;
using ShareExpenses.Shared;
using ShareExpenses.Tests.Infrastructure;
using MemberClaimed = ShareExpenses.Slices.CreateGroup.MemberClaimed;

namespace ShareExpenses.Tests.Slices.AcceptInvite;

/// <summary>
/// The invite flow end to end: Alice creates, adds and invites through the real
/// endpoints; the invitee claims through this slice's, against the real store.
/// </summary>
[Collection(AppCollection.Name)]
public class AcceptInviteIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();
    private readonly UserId _bob = UserId.New();
    private readonly UserId _carol = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    // ── Specs against the real store ──────────────────────────────────────────────

    [Fact]
    public async Task S1_claims_the_invited_slot_and_drops_its_delivery()
    {
        var (groupId, bob, token) = await InvitedBob();

        var response = await Claim(_bob, groupId, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ClaimedBody(groupId, bob), await response.Content.ReadFromJsonAsync<ClaimedBody>());
        Assert.Equal(new MemberClaimed(bob, _bob), (await StreamOf(groupId)).Last());

        await using var session = Store.QuerySession();
        Assert.Null(await session.LoadAsync<InviteDelivery>(bob.Value));
    }

    [Fact]
    public async Task S7_a_used_link_is_not_found_for_anyone_else()
    {
        var (groupId, _, token) = await InvitedBob();
        Assert.Equal(HttpStatusCode.OK, (await Claim(_bob, groupId, token)).StatusCode);

        var response = await Claim(_carol, groupId, token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("invite not found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task S9_tapping_again_after_joining_is_409_with_the_way_in()
    {
        var (groupId, bob, token) = await InvitedBob();
        await Claim(_bob, groupId, token);

        var response = await Claim(_bob, groupId, token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("you're already in this group as Bob", body.GetProperty("detail").GetString());
        Assert.Equal(bob.ToString(), body.GetProperty("memberId").GetString());
        Assert.Equal(groupId.ToString(), body.GetProperty("groupId").GetString());
    }

    [Fact]
    public async Task S5_the_link_dies_at_its_recorded_deadline()
    {
        var (groupId, _, token) = await InvitedBob();
        try
        {
            app.Clock.Offset = TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1);
            Assert.Equal(HttpStatusCode.NotFound, (await Claim(_bob, groupId, token)).StatusCode);
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
        }
    }

    // ── Concurrency: one forwarded link, two people ───────────────────────────────

    [Fact]
    public async Task Two_claimants_one_link_the_loser_gets_409_and_its_retry_404()
    {
        var (groupId, bob, token) = await InvitedBob();

        // Carol fetches and decides; just before she saves, Bob's claim lands.
        app.BeforeNextSave.Arm(async () =>
            Assert.Equal(HttpStatusCode.OK, (await Claim(_bob, groupId, token)).StatusCode));
        Assert.Equal(HttpStatusCode.Conflict, (await Claim(_carol, groupId, token)).StatusCode);

        var retry = await Claim(_carol, groupId, token);

        Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);
        Assert.Contains("invite not found", await retry.Content.ReadAsStringAsync());
        Assert.Equal(new MemberClaimed(bob, _bob), (await StreamOf(groupId)).Last());
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_dead_link_gets_the_same_404()
    {
        var (groupId, _, token) = await InvitedBob();
        var client = app.ClientFor(_carol);

        HttpResponseMessage[] dead =
        [
            await Claim(_carol, groupId, "wrong-token"),
            await Claim(_carol, GroupId.New(), token),
            await client.PostAsJsonAsync("/api/invites/not-a-guid/accept", new { token }),
            await client.PostAsJsonAsync($"/api/invites/{groupId}/accept", new { }),
        ];

        Assert.All(dead, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Single((await Task.WhenAll(dead.Select(r => r.Content.ReadAsStringAsync()))).Distinct());
    }

    [Fact]
    public async Task Claiming_requires_a_signed_in_user()
    {
        var (groupId, _, token) = await InvitedBob();

        var response = await app.CreateClient().PostAsJsonAsync($"/api/invites/{groupId}/accept", new { token });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task After_claiming_the_new_member_may_act_in_the_group()
    {
        var (groupId, _, token) = await InvitedBob();
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.ClientFor(_bob).PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = "Carol" })).StatusCode);

        await Claim(_bob, groupId, token);

        Assert.Equal(HttpStatusCode.Created,
            (await app.ClientFor(_bob).PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = "Carol" })).StatusCode);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Alice creates "Lisbon trip", adds Bob and invites him; returns the token.</summary>
    private async Task<(GroupId Group, MemberId Bob, string Token)> InvitedBob()
    {
        var client = app.ClientFor(_alice);
        var created = await client.PostAsJsonAsync("/api/groups",
            new { groupName = "Lisbon trip", currency = "GBP", memberName = "Alice" });
        var groupId = (await created.Content.ReadFromJsonAsync<CreatedBody>())!.GroupId;
        var added = await client.PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = "Bob" });
        var bob = (await added.Content.ReadFromJsonAsync<AddedBody>())!.MemberId;
        var invited = await client.PostAsJsonAsync(
            $"/api/groups/{groupId}/members/{bob}/invite", new { email = $"bob-{Guid.NewGuid():N}@example.com" });
        var link = (await invited.Content.ReadFromJsonAsync<InvitedBody>())!.Link;
        return (groupId, bob, link[(link.IndexOf('#') + 1)..]);
    }

    private Task<HttpResponseMessage> Claim(UserId user, GroupId groupId, string token) =>
        app.ClientFor(user).PostAsJsonAsync($"/api/invites/{groupId}/accept", new { token });

    private async Task<IReadOnlyList<object>> StreamOf(GroupId groupId)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(groupId.Value)).Select(e => e.Data).ToList();
    }

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);

    private sealed record InvitedBody(string Link);

    private sealed record ClaimedBody(GroupId GroupId, MemberId MemberId);
}
