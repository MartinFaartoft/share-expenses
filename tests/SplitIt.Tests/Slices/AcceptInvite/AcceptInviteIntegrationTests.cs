using System.Net;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SplitIt.Infrastructure.Invites;
using SplitIt.Shared;
using SplitIt.Tests.Infrastructure;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;

namespace SplitIt.Tests.Slices.AcceptInvite;

/// <summary>
/// Join, from the Home screen, against the real store: Alice's group and Bob's invite
/// are seeded as events and an <see cref="Invite"/> document; the invitee, holding an
/// account at the invited address, posts the Join form.
/// </summary>
[Collection(AppCollection.Name)]
public class AcceptInviteIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    // ── Specs against the real store ──────────────────────────────────────────────

    [Fact]
    public async Task S1_claims_the_slot_invited_at_the_users_address_drops_its_invite_and_goes_into_the_group()
    {
        var (bob, bobEmail) = await Account("bob");
        var (groupId, slot) = await InvitedBob(bobEmail);
        var browser = app.BrowserFor(bob);

        // The form as Home renders it, with its token.
        var response = await Join(browser, groupId, await Forms.TokenFrom(browser, "/"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{groupId}", response.Headers.Location?.OriginalString);
        Assert.Equal(new MemberClaimed(slot, bob), (await StreamOf(groupId)).Last());
        await using var session = Store.QuerySession();
        Assert.Empty(await session.Query<Invite>().Where(i => i.GroupId == groupId).ToListAsync());
    }

    [Fact]
    public async Task The_address_matches_case_insensitively()
    {
        var (bob, bobEmail) = await Account("Bob");
        var (groupId, slot) = await InvitedBob(bobEmail.ToUpperInvariant());

        await Join(bob, groupId);

        Assert.Equal(new MemberClaimed(slot, bob), (await StreamOf(groupId)).Last());
    }

    [Fact]
    public async Task S2_another_address_claims_nothing_and_goes_back_home()
    {
        var (_, bobEmail) = await Account("bob");
        var (groupId, _) = await InvitedBob(bobEmail);
        var (carol, _) = await Account("carol");

        var response = await Join(carol, groupId);

        Assert.Equal("/", response.Headers.Location?.OriginalString);
        Assert.DoesNotContain(await StreamOf(groupId), e => e is MemberClaimed c && c.UserId == carol);
    }

    [Fact]
    public async Task S8_joining_again_goes_into_the_group_and_claims_nothing_more()
    {
        var (bob, bobEmail) = await Account("bob");
        var (groupId, _) = await InvitedBob(bobEmail);
        await Join(bob, groupId);

        var again = await Join(bob, groupId);

        Assert.Equal($"/groups/{groupId}", again.Headers.Location?.OriginalString);
        Assert.Single((await StreamOf(groupId)).OfType<MemberClaimed>(), c => c.UserId == bob);
    }

    [Fact]
    public async Task S4_the_invite_dies_at_its_recorded_deadline()
    {
        var (bob, bobEmail) = await Account("bob");
        var (groupId, _) = await InvitedBob(bobEmail);
        try
        {
            app.Clock.Offset = TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1);
            Assert.Equal("/", (await Join(bob, groupId)).Headers.Location?.OriginalString);
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
        }
    }

    // ── Concurrency: one slot, two claims ─────────────────────────────────────────

    [Fact]
    public async Task Two_claims_on_one_slot_the_loser_gets_409_and_its_retry_goes_into_the_group()
    {
        var (bob, bobEmail) = await Account("bob");
        var (groupId, slot) = await InvitedBob(bobEmail);
        // A second account at the same address cannot exist, so the competitor is the
        // same user on another phone: the retry then finds them already a member.
        var otherPhone = app.BrowserFor(bob);
        var otherToken = await Forms.TokenFrom(otherPhone, "/sign-in");
        app.BeforeNextSave.Arm(async () =>
            Assert.Equal(HttpStatusCode.Redirect, (await Join(otherPhone, groupId, otherToken)).StatusCode));

        Assert.Equal(HttpStatusCode.Conflict, (await Join(bob, groupId)).StatusCode);

        Assert.Equal($"/groups/{groupId}", (await Join(bob, groupId)).Headers.Location?.OriginalString);
        Assert.Equal(new MemberClaimed(slot, bob), (await StreamOf(groupId)).Last());
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_dead_invite_goes_back_home()
    {
        var (_, bobEmail) = await Account("bob");
        var (groupId, _) = await InvitedBob(bobEmail);
        var (carol, _) = await Account("carol");
        var (bob2, bob2Email) = await Account("bob2");
        await InvitedBob(bob2Email);
        var browser = app.BrowserFor(bob2);
        var token = await Forms.TokenFrom(browser, "/");

        HttpResponseMessage[] dead =
        [
            await Join(carol, groupId),                                                   // another address
            await Join(browser, GroupId.New(), token),                                    // unknown group
            await Forms.Post(browser, "/invites/not-a-guid/join", token),                 // malformed group
            await Join(browser, groupId, token),                                          // invited elsewhere
        ];

        Assert.All(dead, r => Assert.Equal("/", r.Headers.Location?.OriginalString));
    }

    [Fact]
    public async Task Signed_out_it_redirects_to_sign_in()
    {
        var response = await app.CreateClient(new() { AllowAutoRedirect = false })
            .PostAsync($"/invites/{GroupId.New()}/join", null);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/sign-in", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Without_a_valid_antiforgery_token_it_is_400_and_claims_nothing()
    {
        var (bob, bobEmail) = await Account("bob");
        var (groupId, _) = await InvitedBob(bobEmail);
        var browser = app.BrowserFor(bob);
        await browser.GetAsync("/");

        var response = await Join(browser, groupId, "forged");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(await StreamOf(groupId), e => e is MemberClaimed c && c.UserId == bob);
    }

    [Fact]
    public async Task After_joining_the_new_member_may_see_the_group()
    {
        var (bob, bobEmail) = await Account("bob");
        var (groupId, _) = await InvitedBob(bobEmail);
        Assert.Equal(HttpStatusCode.NotFound, (await app.ClientFor(bob).GetAsync($"/groups/{groupId}")).StatusCode);

        await Join(bob, groupId);

        Assert.Equal(HttpStatusCode.OK, (await app.ClientFor(bob).GetAsync($"/groups/{groupId}")).StatusCode);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private async Task<(UserId User, string Email)> Account(string name)
    {
        var email = $"{name}-{Guid.NewGuid():N}@example.com";
        return (await app.AccountFor(email), email);
    }

    /// <summary>Alice's "Lisbon trip", with Bob added and invited at <paramref name="email"/>.</summary>
    private async Task<(GroupId Group, MemberId Bob)> InvitedBob(string email)
    {
        var group = await new Seed(app).Group(_alice);
        var bob = await group.Member("Bob");
        await group.Invite(bob, email);
        return (group.Id, bob);
    }

    /// <summary>Join as <paramref name="user"/>, from a fresh browser with its own token.</summary>
    private async Task<HttpResponseMessage> Join(UserId user, GroupId groupId)
    {
        var browser = app.BrowserFor(user);
        return await Join(browser, groupId, await Forms.TokenFrom(browser, "/sign-in"));
    }

    private static Task<HttpResponseMessage> Join(HttpClient browser, GroupId groupId, string token) =>
        Forms.Post(browser, $"/invites/{groupId}/join", token);

    private async Task<IReadOnlyList<object>> StreamOf(GroupId groupId)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(groupId.Value)).Select(e => e.Data).ToList();
    }

    [Fact]
    public async Task An_invite_into_an_archived_group_claims_nothing_and_goes_back_home()
    {
        var (bob, bobEmail) = await Account("bob");
        var (groupId, _) = await InvitedBob(bobEmail);
        await new Seed(app).Append(groupId, new SplitIt.Slices.ArchiveGroup.GroupArchived(_alice));

        var response = await Join(bob, groupId);

        Assert.Equal("/", response.Headers.Location?.OriginalString);
        Assert.DoesNotContain(await StreamOf(groupId), e => e is MemberClaimed c && c.UserId == bob);
    }
}
