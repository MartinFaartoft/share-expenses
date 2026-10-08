using System.Net;
using SplitIt.Shared;
using SplitIt.Tests.Infrastructure;
using ExpenseEdited = SplitIt.Slices.EditExpense.ExpenseEdited;
using ExpenseRecorded = SplitIt.Slices.RecordExpense.ExpenseRecorded;

namespace SplitIt.Tests.Slices.ViewExpense;

/// <summary>
/// The Expense screen (slice-13-view-expense.md), as a browser uses it: the sheet htmx
/// asks for, the page a plain request gets, and the group page's cards that open it.
/// </summary>
[Collection(AppCollection.Name)]
public class ViewExpenseIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();
    private readonly UserId _bob = UserId.New();

    private sealed record Lisbon(SeededGroup Group, MemberId Alice, MemberId Bob, MemberId Carol, ExpenseId Dinner);

    /// <summary>Dinner, £90.00 paid by Alice on 1 October and shared by all three.</summary>
    private async Task<Lisbon> SeedLisbon()
    {
        var group = await new Seed(app).Group(_alice, "Lisbon trip", "GBP");
        var bob = await group.Joined("Bob", _bob);
        var carol = await group.Member("Carol");
        var dinner = await group.Expense("Dinner", 9000, group.You, [group.You, bob, carol]);
        return new Lisbon(group, group.You, bob, carol, dinner);
    }

    private static string Path(Lisbon l) => Path(l.Group.Id, l.Dinner);

    private static string Path(GroupId group, ExpenseId expense) => $"/groups/{group}/expenses/{expense}";

    private static async Task<HttpResponseMessage> Get(HttpClient browser, string path, bool htmx)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (htmx)
            request.Headers.Add("HX-Request", "true");
        return await browser.SendAsync(request);
    }

    private static async Task<string> Html(HttpResponseMessage response) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    // ── The sheet ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task For_htmx_the_expense_is_a_sheet_without_the_page_around_it()
    {
        var l = await SeedLisbon();

        var response = await Get(app.BrowserFor(_alice), Path(l), htmx: true);
        var html = await Html(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("""<dialog class="sheet" open""", html.TrimStart());
        Assert.DoesNotContain("<html", html);
        Assert.DoesNotContain("<title>", html);
        Assert.Contains("""<form method="dialog" class="sheet-close"><button type="submit" aria-label="Close">×</button></form>""", html);
        Assert.DoesNotContain(">Close</button>", html);
        Assert.Contains("<h2 id=\"expense-title\">Dinner</h2>", html);
        Assert.Contains("£90.00", html);
        Assert.Contains("You paid · 1 Oct 2026", html);
        Assert.Contains("Shared equally", html);
    }

    [Fact]
    public async Task The_sheet_lists_everyone_with_what_they_owe_and_marks_you()
    {
        var l = await SeedLisbon();

        var html = await Html(await Get(app.BrowserFor(_bob), Path(l), htmx: true));

        Assert.Contains("Alice paid · 1 Oct 2026", html);
        Assert.Matches(@"<span>Alice</span>\s*<span class=""amount"">£30.00</span>", html);
        Assert.Matches(@"<span>Bob \(you\)</span>\s*<span class=""amount"">£30.00</span>", html);
        Assert.Matches(@"<span>Carol</span>\s*<span class=""amount"">£30.00</span>", html);
        Assert.True(html.IndexOf("Alice</span>", StringComparison.Ordinal) < html.IndexOf("Bob (you)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Edit_and_remove_are_the_sheets_to_offer()
    {
        var l = await SeedLisbon();

        var html = await Html(await Get(app.BrowserFor(_alice), Path(l), htmx: true));

        Assert.Contains($"""<a class="icon-button" href="{Path(l)}/edit" aria-label="Edit expense" title="Edit">""", html);
        Assert.Contains($"""<a class="icon-button danger" href="{Path(l)}/remove" aria-label="Remove expense" title="Remove">""", html);
        Assert.DoesNotContain(">Edit</a>", html);
        Assert.DoesNotContain(">Remove</a>", html);
    }

    [Fact]
    public async Task An_edited_expense_shows_its_latest_values()
    {
        var l = await SeedLisbon();
        await l.Group.Seed.Append(l.Group.Id, new ExpenseEdited(
            l.Dinner, "Team dinner", 12050, l.Bob, new EqualSplit([l.Alice, l.Carol]),
            [new Split(l.Alice, 6025), new Split(l.Carol, 6025)], new DateOnly(2026, 9, 28), _bob));

        var html = await Html(await Get(app.BrowserFor(_alice), Path(l), htmx: true));

        Assert.Contains("Team dinner", html);
        Assert.DoesNotContain(">Dinner<", html);
        Assert.Contains("£120.50", html);
        Assert.Contains("Bob paid · 28 Sep 2026", html);
        Assert.DoesNotContain("<span>Bob", html);
    }

    [Fact]
    public async Task A_shares_split_shows_each_weight_and_an_exact_split_each_amount()
    {
        var l = await SeedLisbon();
        var flat = ExpenseId.New();
        var steak = ExpenseId.New();
        await l.Group.Seed.Append(l.Group.Id,
            new ExpenseRecorded(flat, "Flat", 1000, l.Alice,
                new SharesSplit([new MemberShares(l.Alice, 2), new MemberShares(l.Bob, 1)]),
                [new Split(l.Alice, 667), new Split(l.Bob, 333)], new DateOnly(2026, 10, 1), _alice),
            new ExpenseRecorded(steak, "Steak night", 5000, l.Alice,
                new ExactSplit([new MemberAmount(l.Alice, 2000), new MemberAmount(l.Bob, 3000)]),
                [new Split(l.Alice, 2000), new Split(l.Bob, 3000)], new DateOnly(2026, 10, 1), _alice));

        var shares = await Html(await Get(app.BrowserFor(_alice), Path(l.Group.Id, flat), htmx: true));
        var exact = await Html(await Get(app.BrowserFor(_alice), Path(l.Group.Id, steak), htmx: true));

        Assert.Contains("By shares", shares);
        Assert.Contains("Alice (you) ×2", shares);
        Assert.Contains("Bob ×1", shares);
        Assert.Contains("£6.67", shares);
        Assert.Contains("Exact", exact);
        Assert.Contains("£30.00", exact);
        Assert.DoesNotMatch(@"×\d", exact);
    }

    // ── The page ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_htmx_it_is_a_page_with_the_same_content_and_a_way_back()
    {
        var l = await SeedLisbon();

        var response = await Get(app.BrowserFor(_alice), Path(l), htmx: false);
        var html = await Html(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<title>Expense · SplitIt</title>", html);
        Assert.Contains($"""<a class="up" href="/groups/{l.Group.Id}" aria-label="Back">‹</a>""", html);
        Assert.DoesNotContain("<dialog", html);
        Assert.Contains("<h2 id=\"expense-title\">Dinner</h2>", html);
        Assert.Contains("£90.00", html);
        Assert.Contains("Shared equally", html);
        Assert.Contains($"""href="{Path(l)}/edit" aria-label="Edit expense" """, html);
        Assert.Contains($"""href="{Path(l)}/remove" aria-label="Remove expense" """, html);
        Assert.DoesNotContain("sheet-close", html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Either_answer_says_it_depends_on_the_htmx_header(bool htmx)
    {
        var l = await SeedLisbon();

        var response = await Get(app.BrowserFor(_alice), Path(l), htmx);

        Assert.Contains("HX-Request", response.Headers.Vary);
    }

    [Fact]
    public async Task Signed_out_it_redirects_to_sign_in_and_back()
    {
        var l = await SeedLisbon();

        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(Path(l));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/sign-in?returnUrl=%2Fgroups%2F{l.Group.Id}%2Fexpenses%2F{l.Dinner}", response.Headers.Location?.OriginalString);
    }

    // ── Not found ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Whoever_cannot_see_it_gets_the_same_404(bool htmx)
    {
        var l = await SeedLisbon();
        var removed = await l.Group.Expense("Taxi", 3000, l.Alice, [l.Alice]);
        await l.Group.RemoveExpense(removed);
        var mallory = app.BrowserFor(UserId.New());
        var alice = app.BrowserFor(_alice);

        var responses = new[]
        {
            await Get(mallory, Path(l), htmx),
            await Get(mallory, Path(GroupId.New(), l.Dinner), htmx),
            await Get(mallory, $"/groups/nonsense/expenses/{l.Dinner}", htmx),
            await Get(alice, Path(l.Group.Id, ExpenseId.New()), htmx),
            await Get(alice, Path(l.Group.Id, removed), htmx),
            await Get(alice, $"/groups/{l.Group.Id}/expenses/nonsense", htmx),
        };

        foreach (var response in responses)
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await responses[0].Content.ReadAsStringAsync();
        foreach (var response in responses)
            Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task For_htmx_a_missing_expense_is_a_sheet_saying_so()
    {
        var l = await SeedLisbon();

        var response = await Get(app.BrowserFor(_alice), Path(l.Group.Id, ExpenseId.New()), htmx: true);
        var html = await Html(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.StartsWith("""<dialog class="sheet" open""", html.TrimStart());
        Assert.Contains("Expense not found", html);
        Assert.Contains("""<form method="dialog" class="sheet-close"><button type="submit" aria-label="Close">×</button></form>""", html);
        Assert.DoesNotContain("<html", html);
    }

    [Fact]
    public async Task Without_htmx_a_missing_expense_is_the_not_found_page()
    {
        var l = await SeedLisbon();

        var html = await Html(await Get(app.BrowserFor(_alice), Path(l.Group.Id, ExpenseId.New()), htmx: false));

        Assert.Contains("<title>Not found · SplitIt</title>", html);
        Assert.Contains("That expense doesn't exist", html);
    }

    // ── The group page opens it ───────────────────────────────────────────────────

    [Fact]
    public async Task Each_expense_card_is_a_link_that_opens_the_sheet_and_carries_no_actions()
    {
        var l = await SeedLisbon();

        var html = await Html(await app.BrowserFor(_alice).GetAsync($"/groups/{l.Group.Id}"));

        Assert.Contains($"""<a class="card" href="{Path(l)}" hx-get="{Path(l)}" hx-target="#sheet" hx-swap="innerHTML">""", html);
        Assert.Contains("""<div id="sheet"></div>""", html);
        Assert.Contains("You paid · 1 Oct 2026", html);
        Assert.DoesNotContain("/edit", html);
        Assert.DoesNotContain("/remove", html);
    }

    [Fact]
    public async Task A_settlement_card_is_not_a_link()
    {
        var l = await SeedLisbon();
        await l.Group.Settlement(l.Bob, l.Alice, 3000);

        var html = await Html(await app.BrowserFor(_alice).GetAsync($"/groups/{l.Group.Id}"));

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, """<a class="card" """));
    }

    [Fact]
    public async Task The_page_shell_lets_htmx_swap_a_404_and_loads_the_sheet_script()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var html = await Html(await browser.GetAsync($"/groups/{l.Group.Id}"));
        var script = await browser.GetAsync("/js/sheet.js");

        Assert.Contains("""name="htmx-config" """, html);
        Assert.Contains("""{"code":"404","swap":true,"error":false}""", html);
        Assert.Contains("""<script src="/js/sheet.js" defer></script>""", html);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("showModal", await script.Content.ReadAsStringAsync());
    }
}
