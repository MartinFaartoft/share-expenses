using System.Net;
using System.Net.Http.Json;
using ShareExpenses.Shared;
using ShareExpenses.Tests.Infrastructure;

namespace ShareExpenses.Tests.Slices.ViewInvites;

/// <summary>
/// The Your invites screen end to end: invites issued through InviteMember's
/// endpoint, matched to the signed-in user's account address, each group folded by
/// Marten from the real store.
/// </summary>
[Collection(AppCollection.Name)]
public class ViewInvitesIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    [Fact]
    public async Task S1_shows_an_invite_addressed_to_the_users_account()
    {
        var bobEmail = Unique("bob");
        var bob = await app.AccountFor(bobEmail);
        var (groupId, _) = await InvitedBob(bobEmail);

        var invites = await InvitesOf(bob);

        Assert.Equal([new PendingBody(groupId, "Lisbon trip", "Bob", "Alice")], invites);
    }

    [Fact]
    public async Task The_address_matches_case_insensitively()
    {
        var bobEmail = Unique("Bob");
        var bob = await app.AccountFor(bobEmail);
        var (groupId, _) = await InvitedBob(bobEmail.ToUpperInvariant());

        Assert.Equal(groupId, Assert.Single(await InvitesOf(bob)).GroupId);
    }

    [Fact]
    public async Task S2_an_account_at_another_address_sees_nothing()
    {
        await InvitedBob(Unique("bob"));
        var carol = await app.AccountFor(Unique("carol"));

        Assert.Empty(await InvitesOf(carol));
    }

    [Fact]
    public async Task S4_the_invite_dies_at_its_recorded_deadline()
    {
        var bobEmail = Unique("bob");
        var bob = await app.AccountFor(bobEmail);
        await InvitedBob(bobEmail);
        try
        {
            app.Clock.Offset = TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1);
            Assert.Single(await InvitesOf(bob));

            app.Clock.Offset = TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1);
            Assert.Empty(await InvitesOf(bob));
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task Re_inviting_the_slot_elsewhere_takes_the_invite_away()
    {
        var bobEmail = Unique("bob");
        var bob = await app.AccountFor(bobEmail);
        var (groupId, slot) = await InvitedBob(bobEmail);

        await Invite(groupId, slot, Unique("bob-new"));

        Assert.Empty(await InvitesOf(bob));
    }

    [Fact]
    public async Task Requires_a_signed_in_user()
    {
        var response = await app.CreateClient().GetAsync("/api/invites");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static string Unique(string name) => $"{name}-{Guid.NewGuid():N}@example.com";

    /// <summary>Alice creates "Lisbon trip", adds Bob and invites him at <paramref name="email"/>.</summary>
    private async Task<(GroupId Group, MemberId Bob)> InvitedBob(string email)
    {
        var client = app.ClientFor(_alice);
        var created = await client.PostAsJsonAsync("/api/groups",
            new { groupName = "Lisbon trip", currency = "GBP", memberName = "Alice" });
        var groupId = (await created.Content.ReadFromJsonAsync<CreatedBody>())!.GroupId;
        var added = await client.PostAsJsonAsync($"/api/groups/{groupId}/members", new { displayName = "Bob" });
        var bob = (await added.Content.ReadFromJsonAsync<AddedBody>())!.MemberId;
        await Invite(groupId, bob, email);
        return (groupId, bob);
    }

    private async Task Invite(GroupId groupId, MemberId member, string email)
    {
        var response = await app.ClientFor(_alice).PostAsJsonAsync(
            $"/api/groups/{groupId}/members/{member}/invite", new { email });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<IReadOnlyList<PendingBody>> InvitesOf(UserId user)
    {
        var response = await app.ClientFor(user).GetAsync("/api/invites");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InvitesBody>())!.Invites;
    }

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);

    private sealed record InvitesBody(IReadOnlyList<PendingBody> Invites);

    private sealed record PendingBody(GroupId GroupId, string GroupName, string MemberName, string InvitedBy);
}
