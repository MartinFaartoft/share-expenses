using System.Net;
using System.Text.RegularExpressions;
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

namespace SplitIt.Tests.Slices.AddMember;

/// <summary>
/// The Add member screen (slice-02-add-member.md), as a browser uses it: load the
/// screen, keep its cookies, post the form with the token and the member id it carries.
/// </summary>
[Collection(AppCollection.Name)]
public partial class AddMemberIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    private static string UniqueEmail(string name) => $"{name}-{Guid.NewGuid():N}@example.com";

    private Task<SeededGroup> Lisbon() => new Seed(app).Group(_alice, "Lisbon trip", "GBP", "Alice");

    // ── The screen ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_screen_lists_the_people_with_their_status_and_asks_for_a_name_and_an_optional_email()
    {
        var group = await Lisbon();
        await group.Joined("Bob", UserId.New());
        await group.Member("Carol");
        var dave = await group.Member("Dave");
        await group.Invite(dave, UniqueEmail("dave"));

        var html = await ScreenPage(app.BrowserFor(_alice), group.Id);

        Assert.Contains("<title>Add member · SplitIt</title>", html);
        Assert.Contains($"""<a href="/groups/{group.Id}">← Lisbon trip</a>""", html);
        Assert.Matches(
            """<li><span>Alice</span> <span class="status">You</span></li>\s*<li><span>Bob</span> <span class="status">Joined</span></li>\s*<li><span>Carol</span> <span class="status">Not invited</span></li>\s*<li><span>Dave</span> <span class="status">Invited</span></li>""",
            html);
        Assert.Contains($"""<form method="post" action="/groups/{group.Id}/members">""", html);
        Assert.Contains("""name="displayName" value="" maxlength="50" required autofocus""", html);
        Assert.Contains("""<input type="email" name="email" value="" autocomplete="off" />""", html);
        Assert.Contains("Email (optional)", html);
        Assert.NotNull(Forms.TokenIn(html));
        Assert.DoesNotContain("hx-", html);
    }

    [Fact]
    public async Task An_invite_past_its_deadline_is_shown_as_expired()
    {
        var group = await Lisbon();
        var carol = await group.Member("Carol");
        await group.Invite(carol, UniqueEmail("carol"));
        app.Clock.Offset = TimeSpan.FromDays(31);
        try
        {
            var html = await ScreenPage(app.BrowserFor(_alice), group.Id);

            Assert.Contains("""<span>Carol</span> <span class="status">Invite expired</span>""", html);
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task Every_form_carries_a_fresh_member_id()
    {
        var group = await Lisbon();
        var browser = app.BrowserFor(_alice);

        Assert.NotEqual(MemberIdIn(await ScreenPage(browser, group.Id)), MemberIdIn(await ScreenPage(browser, group.Id)));
    }

    [Fact]
    public async Task A_non_member_a_missing_group_and_a_malformed_id_are_the_same_404()
    {
        var group = await Lisbon();
        var mallory = app.BrowserFor(UserId.New());

        var stranger = await mallory.GetAsync($"/groups/{group.Id}/members/new");
        var missing = await mallory.GetAsync($"/groups/{GroupId.New()}/members/new");
        var malformed = await mallory.GetAsync("/groups/nonsense/members/new");

        foreach (var response in new[] { stranger, missing, malformed })
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await stranger.Content.ReadAsStringAsync();
        Assert.Equal(body, await missing.Content.ReadAsStringAsync());
        Assert.Equal(body, await malformed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signed_out_the_screen_redirects_to_sign_in_and_back()
    {
        var group = await Lisbon();

        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync($"/groups/{group.Id}/members/new");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/sign-in?returnUrl=%2Fgroups%2F{group.Id}%2Fmembers%2Fnew", response.Headers.Location?.OriginalString);
    }

    // ── Adding ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task S1_adds_a_placeholder_and_shows_the_screen_again_with_them_in_it()
    {
        var group = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var form = await ScreenPage(browser, group.Id);
        var emailsBefore = app.Emails.Invites.Count();

        var response = await Submit(browser, form, group, "Bob", "");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{group.Id}/members/new", response.Headers.Location?.OriginalString);
        var events = await StreamOf(group.Id);
        var added = Assert.IsType<MemberAdded>(events[^1]);
        Assert.Equal((MemberIdIn(form), "Bob", _alice), (added.MemberId, added.DisplayName, added.By));
        Assert.Equal(4, events.Count);
        Assert.Equal(emailsBefore, app.Emails.Invites.Count());

        Assert.Contains("""<span>Bob</span> <span class="status">Not invited</span>""", await ScreenPage(browser, group.Id));
    }

    [Fact]
    public async Task S10_with_an_email_adds_and_invites_stores_the_invite_and_sends_the_email()
    {
        var group = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var form = await ScreenPage(browser, group.Id);
        var email = UniqueEmail("bob");
        var before = app.Clock.GetUtcNow();

        var response = await Submit(browser, form, group, "Bob", email);

        Assert.Equal($"/groups/{group.Id}/members/new", response.Headers.Location?.OriginalString);
        var events = await StreamOf(group.Id);
        var invited = Assert.IsType<MemberInvited>(events[^1]);
        Assert.Equal(MemberIdIn(form), Assert.IsType<MemberAdded>(events[^2]).MemberId);
        Assert.Equal((MemberIdIn(form), _alice), (invited.MemberId, invited.By));
        Assert.InRange(invited.ExpiresAt, before.AddDays(30), app.Clock.GetUtcNow().AddDays(30));

        await using var session = Store.QuerySession();
        var invite = await session.LoadAsync<Invite>(invited.InviteId.Value);
        Assert.NotNull(invite);
        Assert.Equal((group.Id, MemberIdIn(form), email), (invite.GroupId, invite.MemberId, invite.Email));

        var sent = Assert.Single(app.Emails.Invites, i => i.Email == email);
        Assert.Equal(("Lisbon trip", "Alice", "Bob"), (sent.GroupName, sent.InviterName, sent.MemberName));
        Assert.Contains("""<span>Bob</span> <span class="status">Invited</span>""", await ScreenPage(browser, group.Id));
    }

    [Fact]
    public async Task Name_and_email_are_trimmed()
    {
        var group = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var form = await ScreenPage(browser, group.Id);
        var email = UniqueEmail("dave");

        await Submit(browser, form, group, "  Dave ", $"  {email} ");

        var events = await StreamOf(group.Id);
        Assert.Equal("Dave", Assert.IsType<MemberAdded>(events[^2]).DisplayName);
        Assert.Single(app.Emails.Invites, i => i.Email == email);
    }

    [Fact]
    public async Task A_failing_email_is_not_the_users_failure_and_the_invite_stands()
    {
        var group = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var form = await ScreenPage(browser, group.Id);
        var email = UniqueEmail("bob");
        var previous = app.Emails.FailInvitesTo;
        app.Emails.FailInvitesTo = to => to == email;
        try
        {
            var response = await Submit(browser, form, group, "Bob", email);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.IsType<MemberInvited>((await StreamOf(group.Id))[^1]);
            Assert.DoesNotContain(app.Emails.Invites, i => i.Email == email);
        }
        finally
        {
            app.Emails.FailInvitesTo = previous;
        }
    }

    // ── Rejected: the screen again, with what was entered ────────────────────────

    [Fact]
    public async Task S12_an_email_that_is_not_an_address_adds_no_one_and_comes_back_as_typed()
    {
        var group = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var form = await ScreenPage(browser, group.Id);

        var response = await Submit(browser, form, group, "Bob", "bob");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("""<p class="error" role="alert">email is not a valid address</p>""", html);
        Assert.Contains("""name="displayName" value="Bob" """, html);
        Assert.Contains("""<input type="email" name="email" value="bob" """, html);
        Assert.Equal(MemberIdIn(form), MemberIdIn(html));
        Assert.Equal(3, (await StreamOf(group.Id)).Count);
    }

    [Fact]
    public async Task S7_a_name_already_in_the_group_is_rejected()
    {
        var group = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var form = await ScreenPage(browser, group.Id);

        var html = await (await Submit(browser, form, group, " alice ", "")).Content.ReadAsStringAsync();

        Assert.Contains("a member with that name already exists", html);
        Assert.Equal(3, (await StreamOf(group.Id)).Count);
    }

    [Fact]
    public async Task S6_a_blank_name_is_rejected()
    {
        var group = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var form = await ScreenPage(browser, group.Id);

        var html = await (await Submit(browser, form, group, "   ", "")).Content.ReadAsStringAsync();

        Assert.Contains("name is required", html);
        Assert.Equal(3, (await StreamOf(group.Id)).Count);
    }

    [Fact]
    public async Task S13_the_address_of_someone_already_in_the_group_adds_no_one()
    {
        var group = await Lisbon();
        var email = UniqueEmail("bob");
        await group.Joined("Bob", await app.AccountFor(email));
        var browser = app.BrowserFor(_alice);
        var form = await ScreenPage(browser, group.Id);

        var html = WebUtility.HtmlDecode(await (await Submit(browser, form, group, "Robert", email)).Content.ReadAsStringAsync());

        Assert.Contains($"{email} has already joined as Bob", html);
        Assert.Equal(5, (await StreamOf(group.Id)).Count);
    }

    [Fact]
    public async Task S14_an_address_with_an_open_invite_on_another_slot_adds_no_one()
    {
        var group = await Lisbon();
        var email = UniqueEmail("bob");
        await group.Invite(await group.Member("Bobby"), email);
        var browser = app.BrowserFor(_alice);
        var form = await ScreenPage(browser, group.Id);
        var eventsBefore = (await StreamOf(group.Id)).Count;

        var html = await (await Submit(browser, form, group, "Bob", email.ToUpperInvariant())).Content.ReadAsStringAsync();

        Assert.Contains("that email is already invited as Bobby", html);
        Assert.Equal(eventsBefore, (await StreamOf(group.Id)).Count);
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
        // The other tests swap in a recording sender; this is the registration Production uses.
        // Wolverine must be able to generate the endpoint's code with it (found by `codegen test`
        // in the container image: an opaque factory registration needed service location).
        var resend = new CapturingHandler();
        var settings = new Dictionary<string, string?>
        {
            ["Email:Resend:ApiKey"] = "re_test",
            ["Email:From"] = "noreply@splitit.ftft.dk",
        };
        // Its own database: a second host on the shared one disturbs the fixture host's async daemon.
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
        await using (var session = factory.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream(groupId.Value,
                new GroupCreated(groupId, "Lisbon trip", "GBP", _alice),
                new MemberAdded(aliceSlot, "Alice", _alice),
                new MemberClaimed(aliceSlot, _alice));
            await session.SaveChangesAsync();
        }
        var group = new SeededGroup(new Seed(app), groupId, _alice, aliceSlot);
        var form = await ScreenPage(browser, group.Id);
        var email = UniqueEmail("bob");

        var response = await Submit(browser, form, group, "Bob", email);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var (request, body) = Assert.Single(resend.Received);
        Assert.Equal("https://api.resend.com/emails", request.RequestUri?.ToString());
        Assert.Contains(email, body);
        Assert.Contains("Alice invited you to Lisbon trip on SplitIt", body);
    }

    // ── Submitting twice ──────────────────────────────────────────────────────────

    [Fact]
    public async Task S17_submitting_the_same_form_twice_adds_one_member_and_sends_one_email()
    {
        var group = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var form = await ScreenPage(browser, group.Id);
        var email = UniqueEmail("bob");

        var first = await Submit(browser, form, group, "Bob", email);
        var second = await Submit(browser, form, group, "Bob", email);

        Assert.Equal($"/groups/{group.Id}/members/new", first.Headers.Location?.OriginalString);
        Assert.Equal($"/groups/{group.Id}/members/new", second.Headers.Location?.OriginalString);
        Assert.Single((await StreamOf(group.Id)).OfType<MemberInvited>());
        Assert.Equal(5, (await StreamOf(group.Id)).Count);
        Assert.Single(app.Emails.Invites, i => i.Email == email);
    }

    [Fact]
    public async Task A_malformed_member_id_is_replaced_and_the_member_still_added()
    {
        var group = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var token = Forms.TokenIn(await ScreenPage(browser, group.Id))!;

        var response = await Forms.Post(browser, $"/groups/{group.Id}/members", token,
            ("memberId", "not-a-guid"), ("displayName", "Bob"), ("email", ""));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(4, (await StreamOf(group.Id)).Count);
    }

    [Fact]
    public async Task Two_saves_to_the_group_at_the_same_instant_the_loser_gets_409()
    {
        var group = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var form = await ScreenPage(browser, group.Id);
        app.BeforeNextSave.Arm(async () =>
        {
            await using var session = Store.LightweightSession();
            session.Events.Append(group.Id.Value, new MemberAdded(MemberId.New(), "Dave", _alice));
            await session.SaveChangesAsync();
        });

        var response = await Submit(browser, form, group, "Bob", "");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(4, (await StreamOf(group.Id)).Count);
    }

    // ── Who may ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task S4_a_non_member_posting_is_404_and_adds_nobody()
    {
        var group = await Lisbon();
        var mallory = app.BrowserFor(UserId.New());
        var token = await Forms.TokenFrom(mallory, "/groups/new");

        var response = await Forms.Post(mallory, $"/groups/{group.Id}/members", token,
            ("memberId", MemberId.New().ToString()), ("displayName", "Bob"), ("email", ""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(3, (await StreamOf(group.Id)).Count);
    }

    [Fact]
    public async Task Without_a_valid_antiforgery_token_it_is_400_and_adds_nobody()
    {
        var group = await Lisbon();
        var browser = app.BrowserFor(_alice);
        await ScreenPage(browser, group.Id);

        var response = await Forms.Post(browser, $"/groups/{group.Id}/members", "forged",
            ("memberId", MemberId.New().ToString()), ("displayName", "Bob"), ("email", ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(3, (await StreamOf(group.Id)).Count);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static async Task<string> ScreenPage(HttpClient browser, GroupId group)
    {
        var response = await browser.GetAsync($"/groups/{group}/members/new");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Submits <paramref name="form"/> as shown — its token and member id — with these values.</summary>
    private static Task<HttpResponseMessage> Submit(
        HttpClient browser, string form, SeededGroup group, string displayName, string email) =>
        Forms.Post(browser, $"/groups/{group.Id}/members", Forms.TokenIn(form)!,
            ("memberId", MemberIdIn(form).ToString()), ("displayName", displayName), ("email", email));

    private static MemberId MemberIdIn(string html) =>
        MemberId.From(Guid.Parse(HiddenMemberId().Match(html) is { Success: true } m ? m.Groups[1].Value
            : throw new InvalidOperationException("No member id in the form")));

    private async Task<IReadOnlyList<object>> StreamOf(GroupId group)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(group.Value)).Select(e => e.Data).ToList();
    }

    [GeneratedRegex("""<input type="hidden" name="memberId" value="([^"]+)" />""")]
    private static partial Regex HiddenMemberId();
}
