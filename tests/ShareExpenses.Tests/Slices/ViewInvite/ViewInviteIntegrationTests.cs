using System.Net;
using System.Net.Http.Json;
using ShareExpenses.Shared;
using ShareExpenses.Slices.ViewInvite;
using ShareExpenses.Tests.Infrastructure;

namespace ShareExpenses.Tests.Slices.ViewInvite;

/// <summary>
/// The landing page end to end: real invites issued through slice 3's endpoint,
/// looked up through this slice's, folded by Marten from the real store.
/// </summary>
[Collection(AppCollection.Name)]
public class ViewInviteIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    [Fact]
    public async Task S1_a_fresh_invite_link_shows_the_invite_without_signing_in()
    {
        var (groupId, _, link) = await InvitedBob();

        var response = await Lookup(groupId, TokenOf(link));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new LookupBody("Lisbon trip", "Bob", "Alice"), await response.Content.ReadFromJsonAsync<LookupBody>());
    }

    [Fact]
    public async Task S4_the_link_dies_at_its_recorded_deadline()
    {
        var (groupId, _, link) = await InvitedBob();
        try
        {
            app.Clock.Offset = TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1);
            Assert.Equal(HttpStatusCode.OK, (await Lookup(groupId, TokenOf(link))).StatusCode);

            app.Clock.Offset = TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1);
            Assert.Equal(HttpStatusCode.NotFound, (await Lookup(groupId, TokenOf(link))).StatusCode);
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task S5_re_inviting_retires_the_previous_link_and_the_new_one_works()
    {
        var (groupId, bob, first) = await InvitedBob();
        var second = await Invite(groupId, bob, "bob@new.example.com");

        Assert.Equal(HttpStatusCode.NotFound, (await Lookup(groupId, TokenOf(first))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Lookup(groupId, TokenOf(second))).StatusCode);
    }

    [Fact]
    public async Task Every_dead_link_gets_the_same_answer()
    {
        var (groupId, _, link) = await InvitedBob();
        var token = TokenOf(link);

        HttpResponseMessage[] dead =
        [
            await Lookup(groupId, "wrong-token"),                       // wrong token
            await Lookup(GroupId.New(), token),                         // unknown group
            await app.CreateClient().PostAsJsonAsync($"/api/invites/not-a-guid/lookup", new { token }),
            await app.CreateClient().PostAsJsonAsync($"/api/invites/{groupId}/lookup", new { }),
            await Lookup(groupId, InviteToken.Hash(token)),             // the hash is not the token
        ];

        Assert.All(dead, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        var bodies = await Task.WhenAll(dead.Select(r => r.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        Assert.Contains(Endpoint.NotFoundReason, bodies[0]);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Alice creates "Lisbon trip", adds Bob, invites him; returns the link.</summary>
    private async Task<(GroupId Group, MemberId Bob, string Link)> InvitedBob()
    {
        var client = app.ClientFor(_alice);
        var created = await client.PostAsJsonAsync("/api/groups",
            new { groupName = "Lisbon trip", currency = "GBP", memberName = "Alice" });
        var groupId = (await created.Content.ReadFromJsonAsync<CreatedBody>())!.GroupId;
        var added = await client.PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = "Bob" });
        var bob = (await added.Content.ReadFromJsonAsync<AddedBody>())!.MemberId;
        return (groupId, bob, await Invite(groupId, bob, "bob@example.com"));
    }

    private async Task<string> Invite(GroupId groupId, MemberId member, string email)
    {
        var response = await app.ClientFor(_alice).PostAsJsonAsync(
            $"/api/groups/{groupId}/members/{member}/invite", new { email });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InvitedBody>())!.Link;
    }

    /// <summary>What the landing page does: read the fragment, post it. No sign-in.</summary>
    private Task<HttpResponseMessage> Lookup(GroupId groupId, string token) =>
        app.CreateClient().PostAsJsonAsync($"/api/invites/{groupId}/lookup", new { token });

    private static string TokenOf(string link) => link[(link.IndexOf('#') + 1)..];

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);

    private sealed record InvitedBody(string Link);

    private sealed record LookupBody(string GroupName, string MemberName, string InvitedBy);
}
