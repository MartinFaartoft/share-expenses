using System.Net;
using ShareExpenses.Shared;
using ShareExpenses.Tests.Infrastructure;

namespace ShareExpenses.Tests.Slices.ViewHomepage;

/// <summary>
/// The Home screen end to end: invites seeded as events and <c>Invite</c> documents,
/// matched to the signed-in user's account address, each group folded live by Marten;
/// groups from the asynchronous UserGroups projection, read once the daemon has caught up.
/// </summary>
[Collection(AppCollection.Name)]
public class ViewHomepageIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    private const string BobsInvite = "<strong>Alice</strong> invited you to <strong>Lisbon trip</strong> as Bob.";

    [Fact]
    public async Task S1_offers_an_invite_addressed_to_the_users_account_with_a_join_form_carrying_a_token()
    {
        var bobEmail = Unique("bob");
        var bob = await app.AccountFor(bobEmail);
        var (groupId, _) = await InvitedBob(bobEmail);

        var html = await HomeOf(bob);

        Assert.Contains(BobsInvite, html);
        Assert.Contains($"""<form method="post" action="/invites/{groupId}/join">""", html);
        Assert.Contains(Forms.TokenField, html);
    }

    [Fact]
    public async Task The_address_matches_case_insensitively()
    {
        var bobEmail = Unique("Bob");
        var bob = await app.AccountFor(bobEmail);
        var (groupId, _) = await InvitedBob(bobEmail.ToUpperInvariant());

        Assert.Contains($"/invites/{groupId}/join", await HomeOf(bob));
    }

    [Fact]
    public async Task S2_an_account_at_another_address_sees_nothing()
    {
        await InvitedBob(Unique("bob"));
        var carol = await app.AccountFor(Unique("carol"));

        Assert.DoesNotContain("Invited", await HomeOf(carol));
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
            Assert.Contains(BobsInvite, await HomeOf(bob));

            app.Clock.Offset = TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1);
            Assert.DoesNotContain(BobsInvite, await HomeOf(bob));
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
        var (group, slot) = await InvitedBobIn(bobEmail);

        await group.Invite(slot, Unique("bob-new"));

        Assert.DoesNotContain(BobsInvite, await HomeOf(bob));
    }

    [Fact]
    public async Task Home_lists_the_groups_the_user_created_and_joined_by_name()
    {
        var bob = await app.AccountFor(Unique("bob"));
        var lisbon = await new Seed(app).Group(_alice);
        await lisbon.Joined("Bob", bob);
        var barcelona = await new Seed(app).Group(bob, "Barcelona", "EUR", "Bob");

        await app.ProjectionsCaughtUp();
        var html = await HomeOf(bob);

        Assert.Contains($"""<a href="/groups/{barcelona.Id}">Barcelona</a>""", html);
        Assert.Contains($"""<a href="/groups/{lisbon.Id}">Lisbon trip</a>""", html);
        Assert.True(html.IndexOf("Barcelona", StringComparison.Ordinal) < html.IndexOf("Lisbon trip", StringComparison.Ordinal));
        Assert.DoesNotContain("Invited", html);
    }

    [Fact]
    public async Task A_brand_new_user_is_told_where_groups_will_appear()
    {
        var html = await HomeOf(await app.AccountFor(Unique("dave")));

        Assert.Contains("You're not in any groups yet.", html);
        Assert.DoesNotContain("Invited", html);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static string Unique(string name) => $"{name}-{Guid.NewGuid():N}@example.com";

    /// <summary>Alice's "Lisbon trip", with Bob added and invited at <paramref name="email"/>.</summary>
    private async Task<(SeededGroup Group, MemberId Bob)> InvitedBobIn(string email)
    {
        var group = await new Seed(app).Group(_alice);
        var bob = await group.Member("Bob");
        await group.Invite(bob, email);
        return (group, bob);
    }

    private async Task<(GroupId Group, MemberId Bob)> InvitedBob(string email)
    {
        var (group, bob) = await InvitedBobIn(email);
        return (group.Id, bob);
    }

    private async Task<string> HomeOf(UserId user)
    {
        var response = await app.ClientFor(user).GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // As the user reads it: Razor encodes ' and the like in the markup.
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }
}
