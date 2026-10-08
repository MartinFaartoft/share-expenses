using System.Net;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SplitIt.Shared;
using SplitIt.Slices.RenameGroup;
using SplitIt.Tests.Infrastructure;

namespace SplitIt.Tests.Slices.RenameGroup;

/// <summary>
/// The Rename group screen (slice-14-rename-group.md), as a browser uses it: open the form,
/// post it with its token. Mostly the post and what deciding makes of it; the page itself
/// is only checked for being filled in.
/// </summary>
[Collection(AppCollection.Name)]
public class RenameGroupIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();
    private readonly UserId _bob = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    private async Task<SeededGroup> SeedLisbon()
    {
        var group = await new Seed(app).Group(_alice, "Lisbon trip", "GBP");
        await group.Joined("Bob", _bob);
        await group.Member("Carol");
        return group;
    }

    private static string Path(SeededGroup group) => $"/groups/{group.Id}/rename";

    // ── The form ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_form_is_filled_in_with_the_current_name()
    {
        var group = await SeedLisbon();
        await group.Rename("Porto trip", _bob);

        var html = await FormPage(app.BrowserFor(_alice), group);

        Assert.Contains("<title>Rename group · SplitIt</title>", html);
        Assert.Contains($"""<a class="up" href="/groups/{group.Id}" aria-label="Back">‹</a>""", html);
        Assert.Contains($"""<form method="post" action="{Path(group)}">""", html);
        Assert.Contains("""name="name" value="Porto trip" maxlength="100" required autofocus""", html);
        Assert.NotNull(Forms.TokenIn(html));
    }

    [Fact]
    public async Task The_group_pages_menu_offers_it_beside_add_member()
    {
        var group = await SeedLisbon();

        var html = WebUtility.HtmlDecode(await app.BrowserFor(_alice).GetStringAsync($"/groups/{group.Id}"));

        Assert.Contains($"""<a href="{Path(group)}">Rename group</a>""", html);
        Assert.Contains($"""<a href="/groups/{group.Id}/members/new">Add member</a>""", html);
    }

    [Fact]
    public async Task Whoever_cannot_rename_it_gets_the_same_404_on_the_form()
    {
        var group = await SeedLisbon();
        var mallory = app.BrowserFor(UserId.New());

        var responses = new[]
        {
            await mallory.GetAsync(Path(group)),
            await mallory.GetAsync($"/groups/{GroupId.New()}/rename"),
            await mallory.GetAsync("/groups/nonsense/rename"),
        };

        foreach (var response in responses)
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await responses[0].Content.ReadAsStringAsync();
        foreach (var response in responses)
            Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unclaimed_placeholder_has_no_form()
    {
        var group = await SeedLisbon();

        var response = await app.BrowserFor(UserId.New()).GetAsync(Path(group));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Signed_out_the_form_redirects_to_sign_in_and_back()
    {
        var group = await SeedLisbon();

        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(Path(group));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/sign-in?returnUrl=%2Fgroups%2F{group.Id}%2Frename", response.Headers.Location?.OriginalString);
    }

    // ── Saving ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task S1_renames_the_group_and_goes_back_to_it_under_its_new_name()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, group, await FormPage(browser, group), "Porto trip");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{group.Id}", response.Headers.Location?.OriginalString);
        var renamed = Assert.Single(await RenamesOf(group.Id));
        Assert.Equal(("Porto trip", _alice), (renamed.Name, renamed.By));
        var page = WebUtility.HtmlDecode(await browser.GetStringAsync($"/groups/{group.Id}"));
        Assert.Contains("<title>Porto trip · SplitIt</title>", page);
        Assert.DoesNotContain("Lisbon trip", page);
    }

    [Fact]
    public async Task S2_the_name_is_trimmed()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        await Post(browser, group, await FormPage(browser, group), "  Porto trip   ");

        Assert.Equal("Porto trip", Assert.Single(await RenamesOf(group.Id)).Name);
    }

    [Fact]
    public async Task S3_any_member_may_rename_it_and_is_recorded_as_the_actor()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_bob);

        await Post(browser, group, await FormPage(browser, group), "Porto trip");

        Assert.Equal(_bob, Assert.Single(await RenamesOf(group.Id)).By);
    }

    [Fact]
    public async Task S4_a_group_can_be_renamed_again_and_the_latest_name_stands()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        await Post(browser, group, await FormPage(browser, group), "Porto trip");
        await Post(browser, group, await FormPage(browser, group), "Algarve");

        Assert.Equal(["Porto trip", "Algarve"], (await RenamesOf(group.Id)).Select(r => r.Name));
        Assert.Contains("""value="Algarve" """, await FormPage(browser, group));
    }

    [Fact]
    public async Task S5_the_same_name_appends_nothing_and_goes_back_to_the_group()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, group, await FormPage(browser, group), "Lisbon trip");
        var padded = await Post(browser, group, await FormPage(browser, group), "  Lisbon trip  ");

        Assert.Equal($"/groups/{group.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal($"/groups/{group.Id}", padded.Headers.Location?.OriginalString);
        Assert.Empty(await RenamesOf(group.Id));
    }

    [Fact]
    public async Task Submitting_twice_renames_once_and_both_go_back_to_the_group()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, group);

        var first = await Post(browser, group, form, "Porto trip");
        var second = await Post(browser, group, form, "Porto trip");

        Assert.Equal($"/groups/{group.Id}", first.Headers.Location?.OriginalString);
        Assert.Equal($"/groups/{group.Id}", second.Headers.Location?.OriginalString);
        Assert.Single(await RenamesOf(group.Id));
    }

    [Fact]
    public async Task S6_a_name_is_compared_with_the_current_one_so_going_back_is_a_rename()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        await Post(browser, group, await FormPage(browser, group), "Porto trip");

        await Post(browser, group, await FormPage(browser, group), "Lisbon trip");

        Assert.Equal(["Porto trip", "Lisbon trip"], (await RenamesOf(group.Id)).Select(r => r.Name));
    }

    [Fact]
    public async Task S7_a_change_of_case_is_a_rename()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        await Post(browser, group, await FormPage(browser, group), "lisbon trip");

        Assert.Equal("lisbon trip", Assert.Single(await RenamesOf(group.Id)).Name);
    }

    [Fact]
    public async Task Last_write_wins_a_form_opened_before_another_rename_still_saves()
    {
        var group = await SeedLisbon();
        var alice = app.BrowserFor(_alice);
        var bob = app.BrowserFor(_bob);
        var aliceForm = await FormPage(alice, group);
        var bobForm = await FormPage(bob, group);

        await Post(bob, group, bobForm, "Porto trip");
        var response = await Post(alice, group, aliceForm, "Algarve");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal([("Porto trip", _bob), ("Algarve", _alice)], (await RenamesOf(group.Id)).Select(r => (r.Name, r.By)));
    }

    [Fact]
    public async Task Home_lists_the_group_under_its_new_name_once_the_projection_has_caught_up()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        await Post(browser, group, await FormPage(browser, group), "Porto trip");
        await app.ProjectionsCaughtUp();
        var home = WebUtility.HtmlDecode(await browser.GetStringAsync("/"));

        Assert.Contains($"""<a href="/groups/{group.Id}">Porto trip</a>""", home);
        Assert.DoesNotContain("Lisbon trip", home);
    }

    // ── Rejected: the form again, with what was typed ───────────────────────────

    [Theory]
    [InlineData("", "name is required")]
    [InlineData("   ", "name is required")]
    public async Task S11_a_blank_name_is_shown_back_with_the_reason_and_nothing_is_saved(string typed, string reason)
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, group, await FormPage(browser, group), typed);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"""<p class="error" role="alert">{reason}</p>""", html);
        Assert.Contains($"""name="name" value="{typed}" """, html);
        Assert.Empty(await RenamesOf(group.Id));
    }

    [Fact]
    public async Task A_missing_name_field_is_a_rejection_not_a_server_error()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, group);

        var response = await Forms.Post(browser, Path(group), Forms.TokenIn(form)!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("name is required", await response.Content.ReadAsStringAsync());
        Assert.Empty(await RenamesOf(group.Id));
    }

    [Fact]
    public async Task S12_a_name_over_100_characters_is_rejected_and_shown_back_and_100_is_not()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var tooLong = new string('x', 101);

        var rejected = await Post(browser, group, await FormPage(browser, group), tooLong);
        var accepted = await Post(browser, group, await FormPage(browser, group), new string('x', 100));
        var html = WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync());

        Assert.Contains("name must be at most 100 characters", html);
        Assert.Contains($"""name="name" value="{tooLong}" """, html);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.Equal(new string('x', 100), Assert.Single(await RenamesOf(group.Id)).Name);
    }

    [Fact]
    public async Task A_rejected_rename_leaves_an_earlier_one_standing()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        await Post(browser, group, await FormPage(browser, group), "Porto trip");

        await Post(browser, group, await FormPage(browser, group), "");

        Assert.Equal("Porto trip", Assert.Single(await RenamesOf(group.Id)).Name);
        Assert.Contains("""value="Porto trip" """, await FormPage(browser, group));
    }

    [Fact]
    public async Task A_name_with_markup_is_shown_back_encoded()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var name = "<script>alert(1)</script>" + new string('x', 100);

        var response = await Post(browser, group, await FormPage(browser, group), name);
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("<script>alert(1)", raw);
        Assert.Contains("&lt;script&gt;", raw);
    }

    // ── Who may ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task S9_a_non_member_posting_is_404_and_renames_nothing()
    {
        var group = await SeedLisbon();
        var mallory = app.BrowserFor(UserId.New());
        var token = await Forms.TokenFrom(mallory, "/groups/new");

        var response = await Forms.Post(mallory, Path(group), token, ("name", "Porto trip"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await RenamesOf(group.Id));
    }

    [Fact]
    public async Task A_non_member_is_told_nothing_even_by_a_form_that_would_be_rejected()
    {
        var group = await SeedLisbon();
        var mallory = app.BrowserFor(UserId.New());
        var token = await Forms.TokenFrom(mallory, "/groups/new");

        var response = await Forms.Post(mallory, Path(group), token, ("name", ""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task S8_a_missing_group_and_a_malformed_id_posted_are_404()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var token = Forms.TokenIn(await FormPage(browser, group))!;

        var missing = await Forms.Post(browser, $"/groups/{GroupId.New()}/rename", token, ("name", "Porto trip"));
        var malformed = await Forms.Post(browser, "/groups/nonsense/rename", token, ("name", "Porto trip"));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    [Fact]
    public async Task Two_saves_to_the_group_at_the_same_instant_the_loser_gets_409()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, group);
        app.BeforeNextSave.Arm(async () =>
        {
            await using var session = Store.LightweightSession();
            session.Events.Append(group.Id.Value, new SplitIt.Slices.CreateGroup.MemberAdded(MemberId.New(), "Dave", _alice));
            await session.SaveChangesAsync();
        });

        var response = await Post(browser, group, form, "Porto trip");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await RenamesOf(group.Id));
    }

    [Fact]
    public async Task Without_a_valid_antiforgery_token_it_is_400_and_renames_nothing()
    {
        var group = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        await FormPage(browser, group);

        var response = await Forms.Post(browser, Path(group), "forged", ("name", "Porto trip"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await RenamesOf(group.Id));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static async Task<string> FormPage(HttpClient browser, SeededGroup group)
    {
        var response = await browser.GetAsync(Path(group));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> Post(HttpClient browser, SeededGroup group, string form, string name) =>
        Forms.Post(browser, Path(group), Forms.TokenIn(form)!, ("name", name));

    private async Task<IReadOnlyList<GroupRenamed>> RenamesOf(GroupId group)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(group.Value)).Select(e => e.Data).OfType<GroupRenamed>().ToList();
    }
}
