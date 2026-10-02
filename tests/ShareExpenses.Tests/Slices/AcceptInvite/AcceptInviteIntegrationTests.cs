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
/// endpoints; the invitee, holding an account at the invited address, claims through
/// this slice's, against the real store.
/// </summary>
[Collection(AppCollection.Name)]
public class AcceptInviteIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    // ── Specs against the real store ──────────────────────────────────────────────

    [Fact]
    public async Task S1_claims_the_slot_invited_at_the_users_address_and_drops_its_invite()
    {
        var (bob, bobEmail) = await Account("bob");
        var (groupId, slot) = await InvitedBob(bobEmail);

        var response = await Claim(bob, groupId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ClaimedBody(groupId, slot), await response.Content.ReadFromJsonAsync<ClaimedBody>());
        Assert.Equal(new MemberClaimed(slot, bob), (await StreamOf(groupId)).Last());

        await using var session = Store.QuerySession();
        Assert.Empty(await session.Query<Invite>().Where(i => i.GroupId == groupId).ToListAsync());
    }

    [Fact]
    public async Task The_address_matches_case_insensitively()
    {
        var (bob, bobEmail) = await Account("Bob");
        var (groupId, slot) = await InvitedBob(bobEmail.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.OK, (await Claim(bob, groupId)).StatusCode);
        Assert.Equal(new MemberClaimed(slot, bob), (await StreamOf(groupId)).Last());
    }

    [Fact]
    public async Task S2_another_address_claims_nothing()
    {
        var (_, bobEmail) = await Account("bob");
        var (groupId, _) = await InvitedBob(bobEmail);
        var (carol, _) = await Account("carol");

        var response = await Claim(carol, groupId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("invite not found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task S8_joining_again_after_joining_is_409_with_the_way_in()
    {
        var (bob, bobEmail) = await Account("bob");
        var (groupId, slot) = await InvitedBob(bobEmail);
        await Claim(bob, groupId);

        var response = await Claim(bob, groupId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("you're already in this group as Bob", body.GetProperty("detail").GetString());
        Assert.Equal(slot.ToString(), body.GetProperty("memberId").GetString());
        Assert.Equal(groupId.ToString(), body.GetProperty("groupId").GetString());
    }

    [Fact]
    public async Task S4_the_invite_dies_at_its_recorded_deadline()
    {
        var (bob, bobEmail) = await Account("bob");
        var (groupId, _) = await InvitedBob(bobEmail);
        try
        {
            app.Clock.Offset = TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1);
            Assert.Equal(HttpStatusCode.NotFound, (await Claim(bob, groupId)).StatusCode);
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
        }
    }

    // ── Concurrency: one slot, two claims ─────────────────────────────────────────

    [Fact]
    public async Task Two_claims_on_one_slot_the_loser_gets_409_and_its_retry_404()
    {
        var (bob, bobEmail) = await Account("bob");
        var (groupId, slot) = await InvitedBob(bobEmail);
        // A second account at the same address cannot exist, so the competitor is the
        // same user on another phone: the retry then finds them already a member.
        app.BeforeNextSave.Arm(async () =>
            Assert.Equal(HttpStatusCode.OK, (await Claim(bob, groupId)).StatusCode));

        Assert.Equal(HttpStatusCode.Conflict, (await Claim(bob, groupId)).StatusCode);

        var retry = await Claim(bob, groupId);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Contains("you're already in this group as Bob", await retry.Content.ReadAsStringAsync());
        Assert.Equal(new MemberClaimed(slot, bob), (await StreamOf(groupId)).Last());
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_dead_invite_gets_the_same_404()
    {
        var (_, bobEmail) = await Account("bob");
        var (groupId, _) = await InvitedBob(bobEmail);
        var (carol, _) = await Account("carol");
        var (bob2, bob2Email) = await Account("bob2");
        await InvitedBob(bob2Email);

        HttpResponseMessage[] dead =
        [
            await Claim(carol, groupId),                                                       // another address
            await Claim(bob2, GroupId.New()),                                                  // unknown group
            await app.ClientFor(bob2).PostAsync("/api/invites/not-a-guid/accept", null),        // malformed group
            await Claim(bob2, groupId),                                                        // invited elsewhere
        ];

        Assert.All(dead, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Single((await Task.WhenAll(dead.Select(r => r.Content.ReadAsStringAsync()))).Distinct());
    }

    [Fact]
    public async Task Claiming_requires_a_signed_in_user()
    {
        var response = await app.CreateClient().PostAsync($"/api/invites/{GroupId.New()}/accept", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task After_claiming_the_new_member_may_act_in_the_group()
    {
        var (bob, bobEmail) = await Account("bob");
        var (groupId, _) = await InvitedBob(bobEmail);
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.ClientFor(bob).PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = "Carol" })).StatusCode);

        await Claim(bob, groupId);

        Assert.Equal(HttpStatusCode.Created,
            (await app.ClientFor(bob).PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = "Carol" })).StatusCode);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private async Task<(UserId User, string Email)> Account(string name)
    {
        var email = $"{name}-{Guid.NewGuid():N}@example.com";
        return (await app.AccountFor(email), email);
    }

    /// <summary>Alice creates "Lisbon trip", adds Bob and invites him at <paramref name="email"/>.</summary>
    private async Task<(GroupId Group, MemberId Bob)> InvitedBob(string email)
    {
        var client = app.ClientFor(_alice);
        var created = await client.PostAsJsonAsync("/api/groups",
            new { groupName = "Lisbon trip", currency = "GBP", memberName = "Alice" });
        var groupId = (await created.Content.ReadFromJsonAsync<CreatedBody>())!.GroupId;
        var added = await client.PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = "Bob" });
        var bob = (await added.Content.ReadFromJsonAsync<AddedBody>())!.MemberId;
        var invited = await client.PostAsJsonAsync($"/api/groups/{groupId}/members/{bob}/invite", new { email });
        Assert.Equal(HttpStatusCode.NoContent, invited.StatusCode);
        return (groupId, bob);
    }

    private Task<HttpResponseMessage> Claim(UserId user, GroupId groupId) =>
        app.ClientFor(user).PostAsync($"/api/invites/{groupId}/accept", null);

    private async Task<IReadOnlyList<object>> StreamOf(GroupId groupId)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(groupId.Value)).Select(e => e.Data).ToList();
    }

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);

    private sealed record ClaimedBody(GroupId GroupId, MemberId MemberId);
}
