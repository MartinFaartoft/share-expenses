using System.Net;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SplitIt.Shared;
using SplitIt.Slices.ArchiveGroup;
using SplitIt.Tests.Infrastructure;

namespace SplitIt.Tests.Slices.ArchiveGroup;

/// <summary>
/// The Archive group screen (slice-15-archive-group.md), as a browser uses it: open the confirm
/// page, post its form. It warns and does not block; the page itself is checked for what it says.
/// </summary>
[Collection(AppCollection.Name)]
public class ArchiveGroupIntegrationTests(AppFixture app)
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

    private static string Path(Lisbon l) => $"/groups/{l.Group.Id}/archive";

    // ── The confirm page ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_settled_up_group_is_confirmed_without_a_warning_but_with_the_truth_about_undoing()
    {
        var l = await SeedLisbon();

        var html = await ConfirmPage(app.BrowserFor(_alice), l);

        Assert.Contains("<title>Archive group · SplitIt</title>", html);
        Assert.Contains($"""<a class="up" href="/groups/{l.Group.Id}" aria-label="Back">‹</a>""", html);
        Assert.Contains("<strong>Lisbon trip</strong>", html);
        Assert.Contains("This can't be undone yet.", html);
        Assert.Contains($"""<form method="post" action="{Path(l)}">""", html);
        Assert.Contains(">Archive group</button>", html);
        Assert.Contains($"""<a class="cancel" href="/groups/{l.Group.Id}">Cancel</a>""", html);
        Assert.DoesNotContain("Not everyone is settled up", html);
        Assert.NotNull(Forms.TokenIn(html));
    }

    [Fact]
    public async Task A_group_that_is_not_settled_up_is_warned_with_who_owes_and_who_is_owed()
    {
        var l = await SeedLisbon();
        await l.Group.Expense("Dinner", 9000, l.Alice, [l.Alice, l.Bob, l.Carol]);

        var html = await ConfirmPage(app.BrowserFor(_bob), l);

        Assert.Contains("Not everyone is settled up:", html);
        Assert.Contains("<li>Alice is owed £60.00</li>", html);
        Assert.Contains("<li>Bob (you) owes £30.00</li>", html);
        Assert.Contains("<li>Carol owes £30.00</li>", html);
        Assert.Contains("You can archive it anyway.", html);
        Assert.True(html.IndexOf("Alice is owed", StringComparison.Ordinal) < html.IndexOf("Carol owes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_those_who_are_not_settled_are_listed()
    {
        var l = await SeedLisbon();
        await l.Group.Expense("Taxi", 3000, l.Alice, [l.Alice, l.Bob]);

        var html = await ConfirmPage(app.BrowserFor(_alice), l);

        Assert.Contains("<li>Alice (you) is owed £15.00</li>", html);
        Assert.Contains("<li>Bob owes £15.00</li>", html);
        Assert.DoesNotContain("Carol", html);
    }

    [Fact]
    public async Task Settling_up_clears_the_warning_and_a_new_expense_brings_it_back()
    {
        var l = await SeedLisbon();
        await l.Group.Expense("Dinner", 9000, l.Alice, [l.Alice, l.Bob]);
        await l.Group.Settlement(l.Bob, l.Alice, 4500);
        var browser = app.BrowserFor(_alice);
        Assert.DoesNotContain("Not everyone is settled up", await ConfirmPage(browser, l));

        await l.Group.Expense("Coffee", 1000, l.Bob, [l.Alice, l.Bob]);
        Assert.Contains("Not everyone is settled up", await ConfirmPage(browser, l));
    }

    [Fact]
    public async Task The_confirm_page_of_an_archived_group_goes_back_to_the_group()
    {
        var l = await SeedLisbon();
        await l.Group.Archive();

        var response = await app.BrowserFor(_alice).GetAsync(Path(l));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{l.Group.Id}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task The_group_pages_menu_offers_it_after_the_others()
    {
        var l = await SeedLisbon();

        var html = WebUtility.HtmlDecode(await app.BrowserFor(_alice).GetStringAsync($"/groups/{l.Group.Id}"));

        Assert.Contains($"""<a href="{Path(l)}">Archive group</a>""", html);
        Assert.True(html.IndexOf("Default split", StringComparison.Ordinal) < html.IndexOf("Archive group</a>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Whoever_cannot_archive_it_gets_the_same_404_on_the_page()
    {
        var l = await SeedLisbon();
        var mallory = app.BrowserFor(UserId.New());

        var responses = new[]
        {
            await mallory.GetAsync(Path(l)),
            await mallory.GetAsync($"/groups/{GroupId.New()}/archive"),
            await mallory.GetAsync("/groups/nonsense/archive"),
        };

        foreach (var response in responses)
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await responses[0].Content.ReadAsStringAsync();
        foreach (var response in responses)
            Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signed_out_the_page_redirects_to_sign_in_and_back()
    {
        var l = await SeedLisbon();

        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(Path(l));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/sign-in?returnUrl=%2Fgroups%2F{l.Group.Id}%2Farchive", response.Headers.Location?.OriginalString);
    }

    // ── Archiving ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task S1_archives_the_group_and_goes_back_to_it_where_a_banner_says_so()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await ConfirmPage(browser, l));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{l.Group.Id}", response.Headers.Location?.OriginalString);
        var archived = Assert.Single(await ArchivesOf(l.Group.Id));
        Assert.Equal(_alice, archived.By);
        Assert.Contains("This group is archived.", WebUtility.HtmlDecode(await browser.GetStringAsync($"/groups/{l.Group.Id}")));
    }

    [Fact]
    public async Task S3_a_group_that_is_not_settled_up_is_archived_all_the_same()
    {
        var l = await SeedLisbon();
        await l.Group.Expense("Dinner", 9000, l.Alice, [l.Alice, l.Bob, l.Carol]);
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await ConfirmPage(browser, l));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Single(await ArchivesOf(l.Group.Id));
    }

    [Fact]
    public async Task S2_any_member_may_archive_it_and_is_recorded_as_the_actor()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_bob);

        await Post(browser, l, await ConfirmPage(browser, l));

        Assert.Equal(_bob, Assert.Single(await ArchivesOf(l.Group.Id)).By);
    }

    [Fact]
    public async Task S6_submitting_twice_archives_once_and_both_go_back_to_the_group()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await ConfirmPage(browser, l);

        var first = await Post(browser, l, form);
        var second = await Post(browser, l, form);

        Assert.Equal($"/groups/{l.Group.Id}", first.Headers.Location?.OriginalString);
        Assert.Equal($"/groups/{l.Group.Id}", second.Headers.Location?.OriginalString);
        Assert.Single(await ArchivesOf(l.Group.Id));
    }

    [Fact]
    public async Task Two_members_archiving_at_once_archive_it_once()
    {
        var l = await SeedLisbon();
        var alice = app.BrowserFor(_alice);
        var bob = app.BrowserFor(_bob);
        var aliceForm = await ConfirmPage(alice, l);
        var bobForm = await ConfirmPage(bob, l);

        await Post(alice, l, aliceForm);
        var response = await Post(bob, l, bobForm);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(_alice, Assert.Single(await ArchivesOf(l.Group.Id)).By);
    }

    // ── Who may ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task S5_a_non_member_posting_is_404_and_archives_nothing()
    {
        var l = await SeedLisbon();
        var mallory = app.BrowserFor(UserId.New());
        var token = await Forms.TokenFrom(mallory, "/groups/new");

        var response = await Forms.Post(mallory, Path(l), token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ArchivesOf(l.Group.Id));
    }

    [Fact]
    public async Task S4_a_missing_group_and_a_malformed_id_posted_are_404()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var token = Forms.TokenIn(await ConfirmPage(browser, l))!;

        var missing = await Forms.Post(browser, $"/groups/{GroupId.New()}/archive", token);
        var malformed = await Forms.Post(browser, "/groups/nonsense/archive", token);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    [Fact]
    public async Task Two_saves_to_the_group_at_the_same_instant_the_loser_gets_409()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await ConfirmPage(browser, l);
        app.BeforeNextSave.Arm(async () =>
        {
            await using var session = Store.LightweightSession();
            session.Events.Append(l.Group.Id.Value, new SplitIt.Slices.CreateGroup.MemberAdded(MemberId.New(), "Dave", _alice));
            await session.SaveChangesAsync();
        });

        var response = await Post(browser, l, form);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await ArchivesOf(l.Group.Id));
    }

    [Fact]
    public async Task Without_a_valid_antiforgery_token_it_is_400_and_archives_nothing()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        await ConfirmPage(browser, l);

        var response = await Forms.Post(browser, Path(l), "forged");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await ArchivesOf(l.Group.Id));
    }

    // ── What an archived group is, over HTTP ──────────────────────────────────────

    [Fact]
    public async Task An_archived_group_stays_readable_with_its_history_and_standing_but_offers_no_action()
    {
        var l = await SeedLisbon();
        await l.Group.Expense("Dinner", 9000, l.Alice, [l.Alice, l.Bob, l.Carol]);
        await l.Group.Archive();

        var html = WebUtility.HtmlDecode(await app.BrowserFor(_alice).GetStringAsync($"/groups/{l.Group.Id}"));

        Assert.Contains("This group is archived.", html);
        Assert.Contains("Dinner", html);
        Assert.Contains("You are owed", html);
        Assert.Contains($"""href="/groups/{l.Group.Id}/balances" """.TrimEnd(), html);
        Assert.DoesNotContain("aria-label=\"Add expense\"", html);
        Assert.DoesNotContain("/settle-up", html);
        Assert.DoesNotContain("""class="menu" """.TrimEnd(), html);
        Assert.DoesNotContain("/rename", html);
        Assert.DoesNotContain("/archive", html);
        Assert.Contains($"""<a class="card" href="/groups/{l.Group.Id}/expenses/""", html);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static async Task<string> ConfirmPage(HttpClient browser, Lisbon l)
    {
        var response = await browser.GetAsync(Path(l));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> Post(HttpClient browser, Lisbon l, string form) =>
        Forms.Post(browser, Path(l), Forms.TokenIn(form)!);

    private async Task<IReadOnlyList<GroupArchived>> ArchivesOf(GroupId group)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(group.Value)).Select(e => e.Data).OfType<GroupArchived>().ToList();
    }
}
