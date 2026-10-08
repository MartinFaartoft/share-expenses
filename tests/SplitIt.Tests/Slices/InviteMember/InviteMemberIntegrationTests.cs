using System.Net;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SplitIt.Infrastructure.Identity;
using SplitIt.Infrastructure.Invites;
using SplitIt.Shared;
using SplitIt.Slices.AddMember;
using SplitIt.Tests.Infrastructure;
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;

namespace SplitIt.Tests.Slices.InviteMember;

/// <summary>
/// The Invite member screen (slice-03-invite-member.md), as a browser uses it: open the
/// slot's form, keep its cookies, post it with the token it carries.
/// </summary>
[Collection(AppCollection.Name)]
public class InviteMemberIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    private static string UniqueEmail(string name) => $"{name}-{Guid.NewGuid():N}@example.com";

    private static string Path(GroupId group, MemberId member) => $"/groups/{group}/members/{member}/invite";

    private async Task<(SeededGroup Group, MemberId Bob)> Lisbon()
    {
        var group = await new Seed(app).Group(_alice, "Lisbon trip", "GBP", "Alice");
        return (group, await group.Member("Bob"));
    }

    private static async Task<string> FormPage(HttpClient browser, GroupId group, MemberId member)
    {
        var response = await browser.GetAsync(Path(group, member));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> Submit(HttpClient browser, string form, GroupId group, MemberId member, string email) =>
        Forms.Post(browser, Path(group, member), Forms.TokenIn(form)!, ("email", email));

    private async Task<IReadOnlyList<object>> StreamOf(GroupId group)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(group.Value)).Select(e => e.Data).ToList();
    }

    private async Task<IReadOnlyList<Invite>> InvitesOf(GroupId group, MemberId member)
    {
        await using var session = Store.QuerySession();
        return await session.Query<Invite>().Where(i => i.GroupId == group && i.MemberId == member).ToListAsync();
    }

    // ── The form ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_form_names_the_member_and_asks_for_an_email()
    {
        var (group, bob) = await Lisbon();

        var html = await FormPage(app.BrowserFor(_alice), group.Id, bob);

        Assert.Contains("<title>Invite Bob · SplitIt</title>", html);
        Assert.Contains($"""<a class="up" href="/groups/{group.Id}/members/new" aria-label="Back">‹</a>""", html);
        Assert.Contains($"""<form method="post" action="{Path(group.Id, bob)}">""", html);
        Assert.Contains("""<input type="email" name="email" value="" required autofocus autocomplete="off" />""", html);
        Assert.NotNull(Forms.TokenIn(html));
        Assert.DoesNotContain("hx-", html);
    }

    [Fact]
    public async Task Whoever_cannot_invite_gets_the_same_404()
    {
        var (group, bob) = await Lisbon();
        var alice = group.You;
        var joined = await group.Joined("Carol", UserId.New());
        var mallory = app.BrowserFor(UserId.New());

        var responses = new[]
        {
            await mallory.GetAsync(Path(group.Id, bob)),                                  // not a member
            await mallory.GetAsync(Path(GroupId.New(), bob)),                             // no such group
            await mallory.GetAsync($"/groups/nonsense/members/{bob}/invite"),             // malformed group
            await app.BrowserFor(_alice).GetAsync(Path(group.Id, MemberId.New())),        // no such slot
            await app.BrowserFor(_alice).GetAsync($"/groups/{group.Id}/members/x/invite"), // malformed slot
            await app.BrowserFor(_alice).GetAsync(Path(group.Id, alice)),                 // already joined
            await app.BrowserFor(_alice).GetAsync(Path(group.Id, joined)),                // already joined
        };

        foreach (var response in responses)
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await responses[0].Content.ReadAsStringAsync();
        foreach (var response in responses)
            Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signed_out_it_redirects_to_sign_in_and_back()
    {
        var (group, bob) = await Lisbon();

        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(Path(group.Id, bob));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/sign-in?returnUrl=%2Fgroups%2F{group.Id}%2Fmembers%2F{bob}%2Finvite", response.Headers.Location?.OriginalString);
    }

    // ── Inviting ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Invites_stores_the_invite_and_sends_the_email_then_goes_back_to_the_people()
    {
        var (group, bob) = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, group.Id, bob);
        var email = UniqueEmail("bob");
        var before = app.Clock.GetUtcNow();

        var response = await Submit(browser, form, group.Id, bob, email);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{group.Id}/members/new", response.Headers.Location?.OriginalString);
        var invited = Assert.IsType<MemberInvited>((await StreamOf(group.Id))[^1]);
        Assert.Equal((bob, _alice), (invited.MemberId, invited.By));
        Assert.InRange(invited.ExpiresAt, before.AddDays(30), app.Clock.GetUtcNow().AddDays(30));
        var invite = Assert.Single(await InvitesOf(group.Id, bob));
        Assert.Equal((invited.InviteId.Value, EmailAddress.Normalize(email)), (invite.Id, invite.NormalizedEmail));
        var sent = Assert.Single(app.Emails.Invites, i => i.Email == email);
        Assert.Equal(("Lisbon trip", "Alice", "Bob"), (sent.GroupName, sent.InviterName, sent.MemberName));
    }

    [Fact]
    public async Task Inviting_again_replaces_the_invite_and_sends_again()
    {
        var (group, bob) = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var first = UniqueEmail("bob");
        var second = UniqueEmail("bob-new");
        await Submit(browser, await FormPage(browser, group.Id, bob), group.Id, bob, first);

        var response = await Submit(browser, await FormPage(browser, group.Id, bob), group.Id, bob, second);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var invited = (await StreamOf(group.Id)).OfType<MemberInvited>().ToList();
        Assert.Equal(2, invited.Count);
        var invite = Assert.Single(await InvitesOf(group.Id, bob));
        Assert.Equal(invited[1].InviteId.Value, invite.Id);
        Assert.Equal(EmailAddress.Normalize(second), invite.NormalizedEmail);
        Assert.Single(app.Emails.Invites, i => i.Email == first);
        Assert.Single(app.Emails.Invites, i => i.Email == second);
    }

    [Theory]
    [InlineData("not-an-address", "email is not a valid address")]
    [InlineData("", "email is not a valid address")]
    public async Task A_rejected_invite_shows_why_with_what_was_typed_and_invites_nobody(string email, string reason)
    {
        var (group, bob) = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, group.Id, bob);
        var before = (await StreamOf(group.Id)).Count;

        var response = await Submit(browser, form, group.Id, bob, email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains($"""<p class="error" role="alert">{reason}</p>""", html);
        Assert.Contains($"""name="email" value="{email}" """, html);
        Assert.Equal(before, (await StreamOf(group.Id)).Count);
        Assert.Empty(await InvitesOf(group.Id, bob));
    }

    [Fact]
    public async Task An_address_already_joined_in_the_group_is_rejected()
    {
        var (group, bob) = await Lisbon();
        var address = UniqueEmail("carol");
        await group.Joined("Carol", await app.AccountFor(address));
        var browser = app.BrowserFor(_alice);

        var response = await Submit(browser, await FormPage(browser, group.Id, bob), group.Id, bob, address);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("has already joined as Carol", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        Assert.Empty(await InvitesOf(group.Id, bob));
    }

    [Fact]
    public async Task A_failing_email_is_not_the_users_failure_and_the_invite_stands()
    {
        var (group, bob) = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, group.Id, bob);
        var email = UniqueEmail("bob");
        app.Emails.FailInvitesTo = to => to == email;
        try
        {
            var response = await Submit(browser, form, group.Id, bob, email);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.IsType<MemberInvited>((await StreamOf(group.Id))[^1]);
            Assert.Single(await InvitesOf(group.Id, bob));
            Assert.DoesNotContain(app.Emails.Invites, i => i.Email == email);
        }
        finally
        {
            app.Emails.FailInvitesTo = _ => false;
        }
    }

    [Fact]
    public async Task Two_saves_to_the_group_at_the_same_instant_the_loser_gets_409()
    {
        var (group, bob) = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, group.Id, bob);
        app.BeforeNextSave.Arm(async () =>
        {
            await using var session = Store.LightweightSession();
            session.Events.Append(group.Id.Value, new MemberAdded(MemberId.New(), "Dave", _alice));
            await session.SaveChangesAsync();
        });

        var response = await Submit(browser, form, group.Id, bob, UniqueEmail("bob"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await InvitesOf(group.Id, bob));
    }

    // ── Who may ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_non_member_posting_is_404_and_invites_nobody()
    {
        var (group, bob) = await Lisbon();
        var mallory = app.BrowserFor(UserId.New());
        var token = await Forms.TokenFrom(mallory, "/groups/new");

        var response = await Forms.Post(mallory, Path(group.Id, bob), token, ("email", UniqueEmail("x")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await InvitesOf(group.Id, bob));
    }

    [Fact]
    public async Task Without_a_valid_antiforgery_token_it_is_400_and_invites_nobody()
    {
        var (group, bob) = await Lisbon();
        var browser = app.BrowserFor(_alice);
        await FormPage(browser, group.Id, bob);

        var response = await Forms.Post(browser, Path(group.Id, bob), "forged", ("email", UniqueEmail("x")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await InvitesOf(group.Id, bob));
    }

    // ── With the real sender ─────────────────────────────────────────────────────

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Received { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Received.Add((request, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"abc"}""") };
        }
    }

    private sealed class ProductionLike : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public async Task With_the_resend_sender_registered_as_in_production_the_endpoint_still_builds_and_sends_the_invite()
    {
        // As for Add member: Wolverine must generate this endpoint's code with the Production registration.
        var resend = new CapturingHandler();
        var settings = new Dictionary<string, string?>
        {
            ["Email:Resend:ApiKey"] = "re_test",
            ["Email:From"] = "noreply@splitit.ftft.dk",
        };
        var database = await app.EmptyDatabase();
        await using var factory = app.WithWebHostBuilder(host => host
            .UseSetting("ConnectionStrings:Default", database)
            .ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddEmailSending(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new ProductionLike());
                services.AddHttpClient(nameof(IEmailSender)).ConfigurePrimaryHttpMessageHandler(() => resend);
            }));
        var browser = factory.CreateClient(new() { AllowAutoRedirect = false });
        browser.DefaultRequestHeaders.Add(AppFixture.UserHeader, _alice.ToString());

        var groupId = GroupId.New();
        var aliceSlot = MemberId.New();
        var bob = MemberId.New();
        await using (var session = factory.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream(groupId.Value,
                new GroupCreated(groupId, "Lisbon trip", "GBP", _alice),
                new MemberAdded(aliceSlot, "Alice", _alice),
                new MemberClaimed(aliceSlot, _alice),
                new MemberAdded(bob, "Bob", _alice));
            await session.SaveChangesAsync();
        }
        var form = await FormPage(browser, groupId, bob);
        var email = UniqueEmail("bob");

        var response = await Submit(browser, form, groupId, bob, email);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var (request, body) = Assert.Single(resend.Received);
        Assert.Equal("https://api.resend.com/emails", request.RequestUri?.ToString());
        Assert.Contains(email, body);
        Assert.Contains("Alice invited you to Lisbon trip on SplitIt", body);
    }

    [Fact]
    public async Task An_archived_group_sends_no_invite_and_the_form_says_why()
    {
        var (group, bob) = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, group.Id, bob);
        await group.Archive();

        var response = await Submit(browser, form, group.Id, bob, UniqueEmail("bob"));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("group is archived", html);
        Assert.Empty(await InvitesOf(group.Id, bob));
    }
}
