using System.Net;
using System.Text.RegularExpressions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SplitIt.Shared;
using SplitIt.Tests.Infrastructure;
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;

namespace SplitIt.Tests.Slices.CreateGroup;

/// <summary>
/// The New group screen (slice-01-create-group.md), as a browser uses it: load the
/// form, keep its cookies, post it with the token and the group id it carries.
/// </summary>
[Collection(AppCollection.Name)]
public partial class CreateGroupIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    // ── The form ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_form_asks_for_name_currency_and_your_name_with_dkk_preselected()
    {
        var html = await FormPage(app.BrowserFor(_alice));

        Assert.Contains("<title>New group · SplitIt</title>", html);
        Assert.Contains("""<a href="/">← Home</a>""", html);
        Assert.Contains("""<form method="post" action="/groups">""", html);
        Assert.Contains("""<input name="groupName" value="" maxlength="100" required autofocus />""", html);
        Assert.Contains("""<input name="memberName" value="" maxlength="50" required autocomplete="given-name" />""", html);
        Assert.Contains("""<option value="DKK" selected>DKK</option>""", html);
        Assert.Contains("""<option value="GBP">GBP £</option>""", html);
        Assert.Single(Regex.Matches(html, " selected>"));
        Assert.NotNull(Forms.TokenIn(html));
        Assert.DoesNotContain("hx-", html);
    }

    [Fact]
    public async Task Every_form_carries_a_fresh_group_id()
    {
        var browser = app.BrowserFor(_alice);

        Assert.NotEqual(GroupIdIn(await FormPage(browser)), GroupIdIn(await FormPage(browser)));
    }

    // ── Creating ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task S1_creates_the_group_with_you_in_it_and_goes_into_it()
    {
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser);
        var groupId = GroupIdIn(form);

        var response = await Submit(browser, form, "Lisbon trip", "GBP", "Alice");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{groupId}", response.Headers.Location?.OriginalString);
        var events = await StreamOf(groupId);
        Assert.Equal(new GroupCreated(groupId, "Lisbon trip", "GBP", _alice), events[0]);
        var added = Assert.IsType<MemberAdded>(events[1]);
        Assert.Equal(("Alice", _alice), (added.DisplayName, added.By));
        Assert.Equal(new MemberClaimed(added.MemberId, _alice), events[2]);
        Assert.Equal(3, events.Count);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/groups/{groupId}")).StatusCode);
    }

    [Fact]
    public async Task Inputs_are_trimmed_and_the_currency_upper_cased()
    {
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser);

        await Submit(browser, form, "  Lisbon trip ", "gbp", " Alice ");

        var events = await StreamOf(GroupIdIn(form));
        Assert.Equal(("Lisbon trip", "GBP"), (((GroupCreated)events[0]).Name, ((GroupCreated)events[0]).Currency));
        Assert.Equal("Alice", ((MemberAdded)events[1]).DisplayName);
    }

    [Fact]
    public async Task The_new_group_appears_on_home()
    {
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser);
        await Submit(browser, form, "Lisbon trip", "DKK", "Alice");

        await app.ProjectionsCaughtUp();

        Assert.Contains($"""<a href="/groups/{GroupIdIn(form)}">Lisbon trip</a>""",
            await (await browser.GetAsync("/")).Content.ReadAsStringAsync());
    }

    // ── Rejected: the page again, with what was typed ─────────────────────────────

    [Fact]
    public async Task S5_a_blank_name_is_shown_back_with_the_reason_and_creates_nothing()
    {
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser);
        var groupId = GroupIdIn(form);

        var response = await Submit(browser, form, "Lisbon trip", "GBP", "   ");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("""<p class="error" role="alert">your name is required</p>""", html);
        Assert.Contains("""<input name="groupName" value="Lisbon trip" """, html);
        Assert.Contains("""<option value="GBP" selected>""", html);
        Assert.Equal(groupId, GroupIdIn(html));         // the same form: still one group if fixed and resubmitted
        Assert.Empty(await StreamOf(groupId));
    }

    [Fact]
    public async Task S3_an_unknown_currency_only_a_forged_post_can_send_is_rejected()
    {
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser);

        var html = await (await Submit(browser, form, "Lisbon trip", "XYZ", "Alice")).Content.ReadAsStringAsync();

        Assert.Contains("currency must be a known ISO 4217 code", html);
        Assert.Empty(await StreamOf(GroupIdIn(form)));
    }

    // ── Submitting twice ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Submitting_the_same_form_twice_creates_one_group_and_goes_into_it_both_times()
    {
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser);
        var groupId = GroupIdIn(form);

        var first = await Submit(browser, form, "Lisbon trip", "GBP", "Alice");
        var second = await Submit(browser, form, "Lisbon trip", "GBP", "Alice");

        Assert.Equal($"/groups/{groupId}", first.Headers.Location?.OriginalString);
        Assert.Equal($"/groups/{groupId}", second.Headers.Location?.OriginalString);
        Assert.Equal(3, (await StreamOf(groupId)).Count);
    }

    [Fact]
    public async Task Two_submits_of_one_form_at_the_same_instant_the_loser_gets_409()
    {
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser);
        var groupId = GroupIdIn(form);
        // The other submit saves between this one's check and its save.
        app.BeforeNextSave.Arm(async () =>
        {
            await using var session = Store.LightweightSession();
            session.Events.StartStream(groupId.Value, new GroupCreated(groupId, "Lisbon trip", "GBP", _alice));
            await session.SaveChangesAsync();
        });

        var response = await Submit(browser, form, "Lisbon trip", "GBP", "Alice");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(await StreamOf(groupId));
    }

    [Fact]
    public async Task A_malformed_group_id_is_replaced_and_the_group_still_created()
    {
        var browser = app.BrowserFor(_alice);
        var token = Forms.TokenIn(await FormPage(browser))!;

        var response = await Forms.Post(browser, "/groups", token,
            ("groupId", "not-a-guid"), ("groupName", "Lisbon trip"), ("currency", "GBP"), ("memberName", "Alice"));

        var location = response.Headers.Location?.OriginalString ?? "";
        Assert.Matches("^/groups/[0-9a-f-]{36}$", location);
        Assert.Equal(3, (await StreamOf(GroupId.From(Guid.Parse(location["/groups/".Length..])))).Count);
    }

    [Fact]
    public async Task Someone_elses_group_id_goes_to_its_page_which_is_not_found_and_appends_nothing()
    {
        var bobs = await new Seed(app).Group(UserId.New(), "Bob's group", "EUR", "Bob");
        var browser = app.BrowserFor(_alice);
        var token = Forms.TokenIn(await FormPage(browser))!;

        var response = await Forms.Post(browser, "/groups", token,
            ("groupId", bobs.Id.ToString()), ("groupName", "Lisbon trip"), ("currency", "GBP"), ("memberName", "Alice"));

        Assert.Equal($"/groups/{bobs.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(3, (await StreamOf(bobs.Id)).Count);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/groups/{bobs.Id}")).StatusCode);
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_valid_antiforgery_token_it_is_400_and_creates_nothing()
    {
        var browser = app.BrowserFor(_alice);
        var groupId = GroupIdIn(await FormPage(browser));

        var response = await Forms.Post(browser, "/groups", "forged",
            ("groupId", groupId.ToString()), ("groupName", "Lisbon trip"), ("currency", "GBP"), ("memberName", "Alice"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await StreamOf(groupId));
    }

    [Fact]
    public async Task Signed_out_the_form_redirects_to_sign_in_and_back()
    {
        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/groups/new");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in?returnUrl=%2Fgroups%2Fnew", response.Headers.Location?.OriginalString);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static async Task<string> FormPage(HttpClient browser)
    {
        var response = await browser.GetAsync("/groups/new");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Submits <paramref name="form"/> as shown — its token and group id — with these values.</summary>
    private static Task<HttpResponseMessage> Submit(
        HttpClient browser, string form, string groupName, string currency, string memberName) =>
        Forms.Post(browser, "/groups", Forms.TokenIn(form)!,
            ("groupId", GroupIdIn(form).ToString()), ("groupName", groupName), ("currency", currency), ("memberName", memberName));

    private static GroupId GroupIdIn(string html) =>
        GroupId.From(Guid.Parse(HiddenGroupId().Match(html) is { Success: true } m ? m.Groups[1].Value
            : throw new InvalidOperationException("No group id in the form")));

    private async Task<IReadOnlyList<object>> StreamOf(GroupId groupId)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(groupId.Value)).Select(e => e.Data).ToList();
    }

    [GeneratedRegex("""<input type="hidden" name="groupId" value="([^"]+)" />""")]
    private static partial Regex HiddenGroupId();
}
