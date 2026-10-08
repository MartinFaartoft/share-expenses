using System.Net;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SplitIt.Shared;
using SplitIt.Slices.RemoveExpense;
using SplitIt.Tests.Infrastructure;

namespace SplitIt.Tests.Slices.RemoveExpense;

/// <summary>The Remove expense screen (slice-11-remove-expense.md), as a browser uses it: open the confirm page, post its form.</summary>
[Collection(AppCollection.Name)]
public class RemoveExpenseIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();
    private readonly UserId _bob = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    private static string Path(GroupId group, ExpenseId expense) => $"/groups/{group}/expenses/{expense}/remove";

    private async Task<(SeededGroup Group, MemberId Bob, ExpenseId Dinner)> Lisbon()
    {
        var group = await new Seed(app).Group(_alice, "Lisbon trip", "GBP", "Alice");
        var bob = await group.Joined("Bob", _bob);
        var dinner = await group.Expense("Dinner", 9000, group.You, [group.You, bob]);
        return (group, bob, dinner);
    }

    private static async Task<string> ConfirmPage(HttpClient browser, GroupId group, ExpenseId expense)
    {
        var response = await browser.GetAsync(Path(group, expense));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private async Task<IReadOnlyList<ExpenseRemoved>> RemovalsOf(GroupId group)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(group.Value)).Select(e => e.Data).OfType<ExpenseRemoved>().ToList();
    }

    private async Task<string> GroupPage(GroupId group, UserId user)
    {
        var response = await app.BrowserFor(user).GetAsync($"/groups/{group}");
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_confirm_page_shows_the_expense_and_asks_for_one_tap()
    {
        var (group, _, dinner) = await Lisbon();

        var html = await ConfirmPage(app.BrowserFor(_bob), group.Id, dinner);

        Assert.Contains("<title>Remove expense · SplitIt</title>", html);
        Assert.Contains($"""<a class="up" href="/groups/{group.Id}" aria-label="Back">‹</a>""", html);
        Assert.Contains("<strong>Dinner</strong>", html);
        Assert.Contains("£90.00", html);
        Assert.Contains("Alice paid · 1 Oct 2026", html);
        Assert.Contains($"""<form method="post" action="{Path(group.Id, dinner)}">""", html);
        Assert.NotNull(Forms.TokenIn(html));
        Assert.DoesNotContain("hx-", html);
    }

    [Fact]
    public async Task The_confirm_page_shows_the_expense_as_last_edited()
    {
        var (group, bob, dinner) = await Lisbon();
        await group.Seed.Append(group.Id, new SplitIt.Slices.EditExpense.ExpenseEdited(
            dinner, "Team dinner", 12050, bob, new EqualSplit([group.You, bob]),
            [new Split(group.You, 6025), new Split(bob, 6025)], new DateOnly(2026, 9, 28), _bob));

        var html = await ConfirmPage(app.BrowserFor(_alice), group.Id, dinner);

        Assert.Contains("<strong>Team dinner</strong>", html);
        Assert.Contains("£120.50", html);
        Assert.Contains("Bob paid · 28 Sep 2026", html);
    }

    [Fact]
    public async Task Any_member_removes_it_and_it_leaves_the_history_and_the_balances()
    {
        var (group, bob, dinner) = await Lisbon();
        var browser = app.BrowserFor(_bob);
        var token = Forms.TokenIn(await ConfirmPage(browser, group.Id, dinner))!;

        var response = await Forms.Post(browser, Path(group.Id, dinner), token);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{group.Id}", response.Headers.Location?.OriginalString);
        var removed = Assert.Single(await RemovalsOf(group.Id));
        Assert.Equal((dinner, _bob), (removed.ExpenseId, removed.By));
        var page = await GroupPage(group.Id, _alice);
        Assert.DoesNotContain("Dinner", page);
        Assert.Contains("You're settled up", page);
        Assert.DoesNotContain(Path(group.Id, dinner), page);
    }

    [Fact]
    public async Task Submitting_twice_removes_it_once_and_both_go_back_to_the_group()
    {
        var (group, _, dinner) = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var token = Forms.TokenIn(await ConfirmPage(browser, group.Id, dinner))!;

        var first = await Forms.Post(browser, Path(group.Id, dinner), token);
        var second = await Forms.Post(browser, Path(group.Id, dinner), token);

        Assert.Equal($"/groups/{group.Id}", first.Headers.Location?.OriginalString);
        Assert.Equal($"/groups/{group.Id}", second.Headers.Location?.OriginalString);
        Assert.Single(await RemovalsOf(group.Id));
    }

    [Fact]
    public async Task A_removed_expense_has_no_confirm_page()
    {
        var (group, _, dinner) = await Lisbon();
        await group.RemoveExpense(dinner);

        var response = await app.BrowserFor(_alice).GetAsync(Path(group.Id, dinner));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Whoever_cannot_remove_it_gets_the_same_404()
    {
        var (group, _, dinner) = await Lisbon();
        var mallory = app.BrowserFor(UserId.New());
        var alice = app.BrowserFor(_alice);

        var responses = new[]
        {
            await mallory.GetAsync(Path(group.Id, dinner)),
            await mallory.GetAsync(Path(GroupId.New(), dinner)),
            await mallory.GetAsync($"/groups/nonsense/expenses/{dinner}/remove"),
            await alice.GetAsync(Path(group.Id, ExpenseId.New())),
            await alice.GetAsync($"/groups/{group.Id}/expenses/nonsense/remove"),
        };

        foreach (var response in responses)
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await responses[0].Content.ReadAsStringAsync();
        foreach (var response in responses)
            Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_non_member_posting_is_404_and_removes_nothing()
    {
        var (group, _, dinner) = await Lisbon();
        var mallory = app.BrowserFor(UserId.New());
        var token = await Forms.TokenFrom(mallory, "/groups/new");

        var response = await Forms.Post(mallory, Path(group.Id, dinner), token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await RemovalsOf(group.Id));
    }

    [Fact]
    public async Task An_unknown_expense_posted_is_404_and_removes_nothing()
    {
        var (group, _, dinner) = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var token = Forms.TokenIn(await ConfirmPage(browser, group.Id, dinner))!;

        var response = await Forms.Post(browser, Path(group.Id, ExpenseId.New()), token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await RemovalsOf(group.Id));
    }

    [Fact]
    public async Task Signed_out_it_redirects_to_sign_in_and_back()
    {
        var (group, _, dinner) = await Lisbon();

        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(Path(group.Id, dinner));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/sign-in?returnUrl=%2Fgroups%2F{group.Id}%2Fexpenses%2F{dinner}%2Fremove", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Two_saves_to_the_group_at_the_same_instant_the_loser_gets_409()
    {
        var (group, _, dinner) = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var token = Forms.TokenIn(await ConfirmPage(browser, group.Id, dinner))!;
        app.BeforeNextSave.Arm(async () =>
        {
            await using var session = Store.LightweightSession();
            session.Events.Append(group.Id.Value, new SplitIt.Slices.CreateGroup.MemberAdded(MemberId.New(), "Dave", _alice));
            await session.SaveChangesAsync();
        });

        var response = await Forms.Post(browser, Path(group.Id, dinner), token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await RemovalsOf(group.Id));
    }

    [Fact]
    public async Task Without_a_valid_antiforgery_token_it_is_400_and_removes_nothing()
    {
        var (group, _, dinner) = await Lisbon();
        var browser = app.BrowserFor(_alice);
        await ConfirmPage(browser, group.Id, dinner);

        var response = await Forms.Post(browser, Path(group.Id, dinner), "forged");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await RemovalsOf(group.Id));
    }

    [Fact]
    public async Task An_archived_group_removes_nothing_and_goes_back_to_the_group_where_the_banner_says_why()
    {
        var (group, _, dinner) = await Lisbon();
        var browser = app.BrowserFor(_alice);
        var token = Forms.TokenIn(await ConfirmPage(browser, group.Id, dinner))!;
        await group.Archive();

        var response = await Forms.Post(browser, Path(group.Id, dinner), token);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{group.Id}", response.Headers.Location?.OriginalString);
        Assert.Empty(await RemovalsOf(group.Id));
        Assert.Contains("This group is archived.", await GroupPage(group.Id, _alice));
    }
}
