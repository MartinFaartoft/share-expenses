using System.Net;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SplitIt.Shared;
using SplitIt.Slices.ChangeDefaultSplit;
using SplitIt.Tests.Infrastructure;

namespace SplitIt.Tests.Slices.ChangeDefaultSplit;

/// <summary>
/// The Default split screen (slice-16-change-default-split.md), as a browser uses it: open the
/// form, post it with its token. Mostly the post and what deciding makes of it; the page itself
/// is only checked for being filled in.
/// </summary>
[Collection(AppCollection.Name)]
public class ChangeDefaultSplitIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();
    private readonly UserId _bob = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    private sealed record Lisbon(SeededGroup Group, MemberId Alice, MemberId Bob, MemberId Carol);

    private async Task<Lisbon> SeedLisbon()
    {
        var group = await new Seed(app).Group(_alice, "Lisbon trip", "GBP");
        var bob = await group.Joined("Bob", _bob);
        var carol = await group.Member("Carol");
        return new Lisbon(group, group.You, bob, carol);
    }

    private static string Path(Lisbon l) => $"/groups/{l.Group.Id}/default-split";

    private static (string Name, string Value) Shares(MemberId member, string value) => ($"shares-{member}", value);

    private static (string Name, string Value) In(MemberId member) => ("participants", member.ToString());

    // ── The form ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_form_of_a_group_with_no_default_is_equal_with_everyone_in()
    {
        var l = await SeedLisbon();

        var html = await FormPage(app.BrowserFor(_alice), l);

        Assert.Contains("<title>Default split · SplitIt</title>", html);
        Assert.Contains($"""<a class="up" href="/groups/{l.Group.Id}" aria-label="Back">‹</a>""", html);
        Assert.Contains($"""<form method="post" action="{Path(l)}">""", html);
        Assert.Contains("""<input type="radio" name="mode" value="equal" checked />""", html);
        Assert.Contains("""<input type="radio" name="mode" value="shares" />""", html);
        Assert.DoesNotContain("""value="exact" """, html);
        foreach (var member in new[] { l.Alice, l.Bob, l.Carol })
        {
            Assert.Contains($"""<input type="checkbox" name="participants" value="{member}" checked />""", html);
            Assert.Contains($"""name="shares-{member}" value="1" """, html);
        }
        Assert.NotNull(Forms.TokenIn(html));
    }

    [Fact]
    public async Task The_form_shows_the_default_in_force_with_the_left_out_unchecked()
    {
        var l = await SeedLisbon();
        await l.Group.DefaultSplit("shares", (l.Alice, 2), (l.Carol, 0));

        var html = await FormPage(app.BrowserFor(_alice), l);

        Assert.Contains("""<input type="radio" name="mode" value="shares" checked />""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Alice}" checked />""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Bob}" checked />""", html);
        Assert.DoesNotContain($"""<input type="checkbox" name="participants" value="{l.Carol}" checked />""", html);
        Assert.Contains($"""name="shares-{l.Alice}" value="2" """, html);
        Assert.Contains($"""name="shares-{l.Bob}" value="1" """, html);
        Assert.Contains($"""name="shares-{l.Carol}" value="1" """, html);
    }

    [Fact]
    public async Task The_group_pages_menu_offers_it_beside_rename()
    {
        var l = await SeedLisbon();

        var html = WebUtility.HtmlDecode(await app.BrowserFor(_alice).GetStringAsync($"/groups/{l.Group.Id}"));

        Assert.Contains($"""<a href="{Path(l)}">Default split</a>""", html);
        Assert.Contains($"""<a href="/groups/{l.Group.Id}/rename">Rename group</a>""", html);
    }

    [Fact]
    public async Task Whoever_cannot_change_it_gets_the_same_404_on_the_form()
    {
        var l = await SeedLisbon();
        var mallory = app.BrowserFor(UserId.New());

        var responses = new[]
        {
            await mallory.GetAsync(Path(l)),
            await mallory.GetAsync($"/groups/{GroupId.New()}/default-split"),
            await mallory.GetAsync("/groups/nonsense/default-split"),
        };

        foreach (var response in responses)
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await responses[0].Content.ReadAsStringAsync();
        foreach (var response in responses)
            Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signed_out_the_form_redirects_to_sign_in_and_back()
    {
        var l = await SeedLisbon();

        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(Path(l));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/sign-in?returnUrl=%2Fgroups%2F{l.Group.Id}%2Fdefault-split", response.Headers.Location?.OriginalString);
    }

    // ── Saving ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task S1_saves_a_shares_default_recording_only_the_shares_that_are_not_1()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), "shares",
            In(l.Alice), In(l.Bob), In(l.Carol), Shares(l.Alice, "2"), Shares(l.Bob, "1"), Shares(l.Carol, "1"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{l.Group.Id}", response.Headers.Location?.OriginalString);
        var changed = Assert.Single(await ChangesOf(l.Group.Id));
        Assert.Equal(("shares", _alice), (changed.Mode, changed.By));
        Assert.Equal([new MemberShares(l.Alice, 2)], changed.Shares);
    }

    [Fact]
    public async Task S2_saves_an_equal_default_with_someone_left_out_by_leaving_them_unchecked()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        await Post(browser, l, await FormPage(browser, l), "equal", In(l.Alice), In(l.Bob));

        var changed = Assert.Single(await ChangesOf(l.Group.Id));
        Assert.Equal("equal", changed.Mode);
        Assert.Equal([new MemberShares(l.Carol, 0)], changed.Shares);
    }

    [Fact]
    public async Task A_checked_member_with_zero_shares_is_left_out()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        await Post(browser, l, await FormPage(browser, l), "shares",
            In(l.Alice), In(l.Bob), In(l.Carol), Shares(l.Alice, "0"), Shares(l.Bob, "3"), Shares(l.Carol, "1"));

        Assert.Equal([new MemberShares(l.Alice, 0), new MemberShares(l.Bob, 3)], Assert.Single(await ChangesOf(l.Group.Id)).Shares);
    }

    [Fact]
    public async Task S5_the_shares_are_recorded_in_member_added_order_whatever_order_they_arrive()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        await Post(browser, l, await FormPage(browser, l), "shares",
            In(l.Carol), In(l.Bob), In(l.Alice), Shares(l.Carol, "4"), Shares(l.Bob, "2"), Shares(l.Alice, "3"));

        Assert.Equal([new MemberShares(l.Alice, 3), new MemberShares(l.Bob, 2), new MemberShares(l.Carol, 4)],
            Assert.Single(await ChangesOf(l.Group.Id)).Shares);
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("shares")]
    public async Task The_shares_of_a_member_who_is_not_checked_are_ignored_even_when_unreadable(string mode)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), mode,
            In(l.Alice), In(l.Bob), Shares(l.Alice, "2"), Shares(l.Bob, "1"), Shares(l.Carol, "garbage"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(new MemberShares(l.Carol, 0), Assert.Single(await ChangesOf(l.Group.Id)).Shares.Single(s => s.MemberId == l.Carol));
    }

    [Fact]
    public async Task Equal_ignores_the_shares_typed_even_when_unreadable()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), "equal",
            In(l.Alice), In(l.Bob), Shares(l.Alice, "x"), Shares(l.Bob, "-9"), Shares(l.Carol, ""));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal([new MemberShares(l.Carol, 0)], Assert.Single(await ChangesOf(l.Group.Id)).Shares);
    }

    [Fact]
    public async Task S10_any_member_may_change_it_and_is_recorded_as_the_actor()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_bob);

        await Post(browser, l, await FormPage(browser, l), "equal", In(l.Alice), In(l.Bob));

        Assert.Equal(_bob, Assert.Single(await ChangesOf(l.Group.Id)).By);
    }

    [Fact]
    public async Task S7_a_form_posted_as_shown_changes_nothing_and_goes_back_to_the_group()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), "equal", In(l.Alice), In(l.Bob), In(l.Carol));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{l.Group.Id}", response.Headers.Location?.OriginalString);
        Assert.Empty(await ChangesOf(l.Group.Id));
    }

    [Fact]
    public async Task S6_the_default_in_force_posted_again_changes_nothing()
    {
        var l = await SeedLisbon();
        await l.Group.DefaultSplit("shares", (l.Alice, 2));
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), "shares",
            In(l.Alice), In(l.Bob), In(l.Carol), Shares(l.Alice, "2"), Shares(l.Bob, "1"), Shares(l.Carol, "1"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Single(await ChangesOf(l.Group.Id));
    }

    [Fact]
    public async Task Submitting_twice_changes_it_once_and_both_go_back_to_the_group()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l);

        var first = await Post(browser, l, form, "equal", In(l.Alice), In(l.Bob));
        var second = await Post(browser, l, form, "equal", In(l.Alice), In(l.Bob));

        Assert.Equal($"/groups/{l.Group.Id}", first.Headers.Location?.OriginalString);
        Assert.Equal($"/groups/{l.Group.Id}", second.Headers.Location?.OriginalString);
        Assert.Single(await ChangesOf(l.Group.Id));
    }

    [Fact]
    public async Task S8_back_to_everyone_after_a_change_is_a_change()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        await Post(browser, l, await FormPage(browser, l), "equal", In(l.Alice), In(l.Bob));

        await Post(browser, l, await FormPage(browser, l), "equal", In(l.Alice), In(l.Bob), In(l.Carol));

        Assert.Equal([[new MemberShares(l.Carol, 0)], []], (await ChangesOf(l.Group.Id)).Select(c => c.Shares.ToArray()));
    }

    [Fact]
    public async Task A_member_who_is_not_a_slot_of_the_group_is_ignored()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), "equal",
            In(l.Alice), In(l.Bob), In(l.Carol), ("participants", MemberId.New().ToString()), ("participants", "not-a-guid"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Empty(await ChangesOf(l.Group.Id));
    }

    [Fact]
    public async Task The_new_default_is_what_the_form_shows_next()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        await Post(browser, l, await FormPage(browser, l), "shares",
            In(l.Alice), In(l.Bob), Shares(l.Alice, "2"), Shares(l.Bob, "1"), Shares(l.Carol, "5"));

        var html = await FormPage(browser, l);

        Assert.Contains("""<input type="radio" name="mode" value="shares" checked />""", html);
        Assert.Contains($"""name="shares-{l.Alice}" value="2" """, html);
        Assert.DoesNotContain($"""<input type="checkbox" name="participants" value="{l.Carol}" checked />""", html);
    }

    // ── Rejected: the form again, with what was entered ──────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1,5")]
    [InlineData("2 shares")]
    [InlineData("99999999999")]
    public async Task A_share_that_is_not_a_whole_number_is_shown_back_as_typed_and_nothing_is_saved(string typed)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), "shares",
            In(l.Alice), In(l.Bob), In(l.Carol), Shares(l.Alice, "2"), Shares(l.Bob, typed), Shares(l.Carol, "1"));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("""<p class="error" role="alert">every share must be a whole number</p>""", html);
        Assert.Contains("""<input type="radio" name="mode" value="shares" checked />""", html);
        Assert.Contains($"""name="shares-{l.Alice}" value="2" """, html);
        Assert.Contains($"""name="shares-{l.Bob}" value="{typed}" """, html);
        Assert.Empty(await ChangesOf(l.Group.Id));
    }

    [Fact]
    public async Task A_checked_member_with_no_shares_field_at_all_is_a_shape_error()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), "shares",
            In(l.Alice), In(l.Bob), Shares(l.Alice, "2"));

        Assert.Contains("every share must be a whole number", await response.Content.ReadAsStringAsync());
        Assert.Empty(await ChangesOf(l.Group.Id));
    }

    [Fact]
    public async Task S18_a_negative_share_is_rejected_by_deciding_and_shown_back()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), "shares",
            In(l.Alice), In(l.Bob), Shares(l.Alice, "2"), Shares(l.Bob, "-1"));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("every share must be zero or more", html);
        Assert.Contains($"""name="shares-{l.Bob}" value="-1" """, html);
        Assert.Empty(await ChangesOf(l.Group.Id));
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("shares")]
    public async Task S19_nobody_checked_is_rejected_by_deciding_in_every_mode(string mode)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), mode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("at least one member must share by default", html);
        Assert.Contains($"""<input type="radio" name="mode" value="{mode}" checked />""", html);
        Assert.DoesNotMatch(@"type=""checkbox""[^>]*checked", html);
        Assert.Empty(await ChangesOf(l.Group.Id));
    }

    [Fact]
    public async Task S19b_everyone_checked_with_zero_shares_is_nobody_too()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), "shares",
            In(l.Alice), In(l.Bob), In(l.Carol), Shares(l.Alice, "0"), Shares(l.Bob, "0"), Shares(l.Carol, "0"));

        Assert.Contains("at least one member must share by default", await response.Content.ReadAsStringAsync());
        Assert.Empty(await ChangesOf(l.Group.Id));
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData("Equal")]
    public async Task S14_a_mode_that_is_not_equal_or_shares_is_a_rejection_not_a_server_error(string mode)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), mode, In(l.Alice), In(l.Bob));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("default split must be equal or shares", html);
        Assert.Contains("""<input type="radio" name="mode" value="equal" checked />""", html);
        Assert.Empty(await ChangesOf(l.Group.Id));
    }

    [Fact]
    public async Task A_missing_mode_field_is_a_rejection_not_a_server_error()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l);

        var response = await Forms.Post(browser, Path(l), Forms.TokenIn(form)!, In(l.Alice));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("default split must be equal or shares", await response.Content.ReadAsStringAsync());
        Assert.Empty(await ChangesOf(l.Group.Id));
    }

    [Fact]
    public async Task A_rejected_form_keeps_what_was_typed_for_every_member_and_who_was_checked()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), "shares",
            In(l.Alice), In(l.Carol), Shares(l.Alice, "3"), Shares(l.Bob, "7"), Shares(l.Carol, "oops"));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Alice}" checked />""", html);
        Assert.DoesNotContain($"""<input type="checkbox" name="participants" value="{l.Bob}" checked />""", html);
        Assert.Contains($"""name="shares-{l.Alice}" value="3" """, html);
        Assert.Contains($"""name="shares-{l.Bob}" value="7" """, html);
        Assert.Contains($"""name="shares-{l.Carol}" value="oops" """, html);
    }

    [Fact]
    public async Task A_rejection_leaves_the_default_in_force_standing()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        await Post(browser, l, await FormPage(browser, l), "equal", In(l.Alice), In(l.Bob));

        await Post(browser, l, await FormPage(browser, l), "equal");

        Assert.Single(await ChangesOf(l.Group.Id));
        Assert.DoesNotMatch($"""participants" value="{l.Carol}" checked""", await FormPage(browser, l));
    }

    // ── Who may ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task S12_a_non_member_posting_is_404_and_changes_nothing()
    {
        var l = await SeedLisbon();
        var mallory = app.BrowserFor(UserId.New());
        var token = await Forms.TokenFrom(mallory, "/groups/new");

        var response = await Forms.Post(mallory, Path(l), token, ("mode", "equal"), In(l.Alice));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ChangesOf(l.Group.Id));
    }

    [Fact]
    public async Task A_non_member_is_told_nothing_even_by_a_form_that_would_be_rejected()
    {
        var l = await SeedLisbon();
        var mallory = app.BrowserFor(UserId.New());
        var token = await Forms.TokenFrom(mallory, "/groups/new");

        var response = await Forms.Post(mallory, Path(l), token, ("mode", "exact"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task S11_a_missing_group_and_a_malformed_id_posted_are_404()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var token = Forms.TokenIn(await FormPage(browser, l))!;

        var missing = await Forms.Post(browser, $"/groups/{GroupId.New()}/default-split", token, ("mode", "equal"), In(l.Alice));
        var malformed = await Forms.Post(browser, "/groups/nonsense/default-split", token, ("mode", "equal"), In(l.Alice));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    [Fact]
    public async Task Two_saves_to_the_group_at_the_same_instant_the_loser_gets_409()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l);
        app.BeforeNextSave.Arm(async () =>
        {
            await using var session = Store.LightweightSession();
            session.Events.Append(l.Group.Id.Value, new SplitIt.Slices.CreateGroup.MemberAdded(MemberId.New(), "Dave", _alice));
            await session.SaveChangesAsync();
        });

        var response = await Post(browser, l, form, "equal", In(l.Alice), In(l.Bob));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await ChangesOf(l.Group.Id));
    }

    [Fact]
    public async Task Without_a_valid_antiforgery_token_it_is_400_and_changes_nothing()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        await FormPage(browser, l);

        var response = await Forms.Post(browser, Path(l), "forged", ("mode", "equal"), In(l.Alice));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await ChangesOf(l.Group.Id));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static async Task<string> FormPage(HttpClient browser, Lisbon l)
    {
        var response = await browser.GetAsync(Path(l));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Posts the mode and the other <paramref name="fields"/> with the token <paramref name="form"/> carries.</summary>
    private static Task<HttpResponseMessage> Post(
        HttpClient browser, Lisbon l, string form, string mode, params (string Name, string Value)[] fields) =>
        Forms.Post(browser, Path(l), Forms.TokenIn(form)!, [("mode", mode), .. fields]);

    private async Task<IReadOnlyList<GroupDefaultSplitChanged>> ChangesOf(GroupId group)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(group.Value)).Select(e => e.Data).OfType<GroupDefaultSplitChanged>().ToList();
    }

    [Fact]
    public async Task S13_an_archived_group_keeps_its_default_and_the_form_says_why()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l);
        await l.Group.Archive();

        var response = await Post(browser, l, form, "equal", In(l.Alice), In(l.Bob));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("""<p class="error" role="alert">group is archived</p>""", html);
        Assert.Empty(await ChangesOf(l.Group.Id));
    }
}
