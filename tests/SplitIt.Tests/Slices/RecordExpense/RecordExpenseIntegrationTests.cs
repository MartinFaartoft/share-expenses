using System.Net;
using System.Text.RegularExpressions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SplitIt.Shared;
using SplitIt.Slices.RecordExpense;
using SplitIt.Tests.Infrastructure;

namespace SplitIt.Tests.Slices.RecordExpense;

/// <summary>
/// The Add expense screen (slice-06-record-expense.md), as a browser uses it: load the
/// form, keep its cookies, post it with the token and the expense id it carries.
/// </summary>
[Collection(AppCollection.Name)]
public partial class RecordExpenseIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();
    private readonly UserId _bob = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    private sealed record Lisbon(SeededGroup Group, MemberId Alice, MemberId Bob, MemberId Carol);

    private async Task<Lisbon> SeedLisbon(string currency = "GBP")
    {
        var group = await new Seed(app).Group(_alice, "Lisbon trip", currency);
        var bob = await group.Joined("Bob", _bob);
        var carol = await group.Member("Carol");
        return new Lisbon(group, group.You, bob, carol);
    }

    private string Today => app.Clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd");

    // ── The form ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_form_offers_every_slot_with_you_paying_and_everyone_sharing_today()
    {
        var l = await SeedLisbon();

        var html = await FormPage(app.BrowserFor(_alice), l.Group.Id);

        Assert.Contains("<title>Add expense · SplitIt</title>", html);
        Assert.Contains($"""<a href="/groups/{l.Group.Id}">← Lisbon trip</a>""", html);
        Assert.Contains($"""<form method="post" action="/groups/{l.Group.Id}/expenses">""", html);
        Assert.Contains("""<input type="hidden" name="mode" value="equal" />""", html);
        Assert.Contains("""<input name="description" value="" maxlength="100" required autofocus />""", html);
        Assert.Contains("Amount (GBP £)", html);
        Assert.Contains("""inputmode="decimal" """, html);
        Assert.Contains("""placeholder="0.00" """, html);
        Assert.Contains($"""<option value="{l.Alice}" selected>Alice (you)</option>""", html);
        Assert.Contains($"""<option value="{l.Bob}">Bob</option>""", html);
        Assert.Contains($"""<option value="{l.Carol}">Carol</option>""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Alice}" checked />""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Bob}" checked />""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Carol}" checked />""", html);
        Assert.Contains($"""Someone missing? <a href="/groups/{l.Group.Id}/members/new">Add a member</a>""", html);
        Assert.True(html.IndexOf(l.Alice.ToString(), StringComparison.Ordinal) < html.IndexOf(l.Bob.ToString(), StringComparison.Ordinal));
        Assert.True(html.IndexOf(l.Bob.ToString(), StringComparison.Ordinal) < html.IndexOf(l.Carol.ToString(), StringComparison.Ordinal));
        Assert.Contains($"""<input type="date" name="paidOn" value="{Today}" max="{DateOnly.Parse(Today).AddDays(1):yyyy-MM-dd}" required />""", html);
        Assert.NotNull(Forms.TokenIn(html));
        Assert.DoesNotContain("hx-", html);
    }

    [Fact]
    public async Task The_amount_is_asked_for_in_the_currencys_decimals()
    {
        var yen = await SeedLisbon("JPY");

        var html = await FormPage(app.BrowserFor(_alice), yen.Group.Id);

        Assert.Contains("""placeholder="0" """, html);
    }

    [Fact]
    public async Task Every_form_carries_a_fresh_expense_id()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        Assert.NotEqual(ExpenseIdIn(await FormPage(browser, l.Group.Id)), ExpenseIdIn(await FormPage(browser, l.Group.Id)));
    }

    [Fact]
    public async Task A_placeholder_or_a_claimed_slot_is_offered_to_whoever_opens_the_form()
    {
        var l = await SeedLisbon();

        var html = await FormPage(app.BrowserFor(_bob), l.Group.Id);

        Assert.Contains($"""<option value="{l.Bob}" selected>Bob (you)</option>""", html);
        Assert.Contains($"""<option value="{l.Alice}">Alice</option>""", html);
    }

    [Fact]
    public async Task A_non_member_a_missing_group_and_a_malformed_id_are_the_same_404()
    {
        var l = await SeedLisbon();
        var mallory = app.BrowserFor(UserId.New());

        var stranger = await mallory.GetAsync($"/groups/{l.Group.Id}/expenses/new");
        var missing = await mallory.GetAsync($"/groups/{GroupId.New()}/expenses/new");
        var malformed = await mallory.GetAsync("/groups/nonsense/expenses/new");

        foreach (var response in new[] { stranger, missing, malformed })
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await stranger.Content.ReadAsStringAsync();
        Assert.Equal(body, await missing.Content.ReadAsStringAsync());
        Assert.Equal(body, await malformed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signed_out_the_form_redirects_to_sign_in_and_back()
    {
        var l = await SeedLisbon();

        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync($"/groups/{l.Group.Id}/expenses/new");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/sign-in?returnUrl=%2Fgroups%2F{l.Group.Id}%2Fexpenses%2Fnew", response.Headers.Location?.OriginalString);
    }

    // ── Recording ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task S1_records_an_equal_split_and_goes_back_to_the_group()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l.Group.Id);

        var response = await Submit(browser, form, l, description: "Dinner", amount: "90.00");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{l.Group.Id}", response.Headers.Location?.OriginalString);
        var recorded = Assert.Single(await ExpensesOf(l.Group.Id));
        Assert.Equal(ExpenseIdIn(form), recorded.ExpenseId);
        Assert.Equal(("Dinner", 9000L, l.Alice, _alice), (recorded.Description, recorded.AmountMinor, recorded.PayerMemberId, recorded.By));
        Assert.Equal(new EqualSplit([l.Alice, l.Bob, l.Carol]), recorded.Split);
        Assert.Equal([new Split(l.Alice, 3000), new Split(l.Bob, 3000), new Split(l.Carol, 3000)], recorded.Splits);
        Assert.Equal(DateOnly.Parse(Today), recorded.PaidOn);
    }

    [Fact]
    public async Task The_payer_and_the_sharers_are_the_ones_chosen()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l.Group.Id);

        await Submit(browser, form, l, description: "Bob's ticket", amount: "45", payer: l.Alice, participants: [l.Bob]);

        var recorded = Assert.Single(await ExpensesOf(l.Group.Id));
        Assert.Equal(new EqualSplit([l.Bob]), recorded.Split);
        Assert.Equal([new Split(l.Bob, 4500)], recorded.Splits);
    }

    [Theory]
    [InlineData("GBP", "120.50", 12050)]
    [InlineData("GBP", "120,50", 12050)]
    [InlineData("GBP", "120", 12000)]
    [InlineData("JPY", "1200", 1200)]
    public async Task The_amount_is_typed_in_the_currencys_units(string currency, string typed, long minor)
    {
        var l = await SeedLisbon(currency);
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l.Group.Id);

        await Submit(browser, form, l, description: "Dinner", amount: typed);

        Assert.Equal(minor, Assert.Single(await ExpensesOf(l.Group.Id)).AmountMinor);
    }

    [Fact]
    public async Task Any_member_may_record_and_is_recorded_as_the_actor()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_bob);
        var form = await FormPage(browser, l.Group.Id);

        await Submit(browser, form, l, description: "Dinner", amount: "90", payer: l.Bob);

        var recorded = Assert.Single(await ExpensesOf(l.Group.Id));
        Assert.Equal((_bob, l.Bob), (recorded.By, recorded.PayerMemberId));
    }

    // ── Rejected: the form again, with what was entered ──────────────────────────

    [Fact]
    public async Task S16_a_blank_description_is_shown_back_with_the_reason_and_records_nothing()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l.Group.Id);

        var response = await Submit(browser, form, l, description: "   ", amount: "12.5", payer: l.Bob,
            participants: [l.Alice, l.Carol], paidOn: "2026-06-15");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("""<p class="error" role="alert">description is required</p>""", html);
        Assert.Contains("""name="amount" value="12.5" """, html);
        Assert.Contains($"""<option value="{l.Bob}" selected>Bob</option>""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Alice}" checked />""", html);
        Assert.DoesNotContain($"""<input type="checkbox" name="participants" value="{l.Bob}" checked />""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Carol}" checked />""", html);
        Assert.Contains("""name="paidOn" value="2026-06-15" """, html);
        Assert.Equal(ExpenseIdIn(form), ExpenseIdIn(html));
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("12.345")]
    [InlineData("1,234.50")]
    public async Task S31_an_unreadable_amount_is_shown_back_as_typed(string typed)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l.Group.Id);

        var html = WebUtility.HtmlDecode(await (await Submit(browser, form, l, description: "Dinner", amount: typed)).Content.ReadAsStringAsync());

        Assert.Contains("""<p class="error" role="alert">amount must be a number</p>""", html);
        Assert.Contains($"""name="amount" value="{typed}" """, html);
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    [Fact]
    public async Task S23_nobody_sharing_is_rejected_by_deciding()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l.Group.Id);

        var html = await (await Submit(browser, form, l, description: "Dinner", amount: "90", participants: [])).Content.ReadAsStringAsync();

        Assert.Contains("at least one participant is required", html);
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    [Fact]
    public async Task S28_a_date_beyond_tomorrow_is_rejected_and_an_unreadable_one_is_missing()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l.Group.Id);
        var later = DateOnly.Parse(Today).AddDays(2).ToString("yyyy-MM-dd");

        var tooLate = await (await Submit(browser, form, l, description: "Dinner", amount: "90", paidOn: later)).Content.ReadAsStringAsync();
        var unreadable = await (await Submit(browser, form, l, description: "Dinner", amount: "90", paidOn: "yesterday")).Content.ReadAsStringAsync();

        Assert.Contains("date cannot be in the future", tooLate);
        Assert.Contains("date is required", unreadable);
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    [Fact]
    public async Task A_mode_the_form_does_not_build_is_a_rejection_not_a_server_error()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l.Group.Id);

        var response = await Submit(browser, form, l, description: "Dinner", amount: "90", mode: "shares");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("split mode not supported", html);
        Assert.Contains("""<input type="hidden" name="mode" value="equal" />""", html);
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    // ── Submitting twice ──────────────────────────────────────────────────────────

    [Fact]
    public async Task S32_submitting_the_same_form_twice_records_one_expense_and_goes_back_both_times()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l.Group.Id);

        var first = await Submit(browser, form, l, description: "Dinner", amount: "90");
        var second = await Submit(browser, form, l, description: "Dinner", amount: "90");

        Assert.Equal($"/groups/{l.Group.Id}", first.Headers.Location?.OriginalString);
        Assert.Equal($"/groups/{l.Group.Id}", second.Headers.Location?.OriginalString);
        Assert.Single(await ExpensesOf(l.Group.Id));
    }

    [Fact]
    public async Task A_malformed_expense_id_is_replaced_and_the_expense_still_recorded()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var token = Forms.TokenIn(await FormPage(browser, l.Group.Id))!;

        var response = await Forms.Post(browser, $"/groups/{l.Group.Id}/expenses", token,
            ("expenseId", "not-a-guid"), ("mode", "equal"), ("description", "Dinner"), ("amount", "90"),
            ("payer", l.Alice.ToString()), ("participants", l.Alice.ToString()), ("paidOn", Today));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Single(await ExpensesOf(l.Group.Id));
    }

    [Fact]
    public async Task Two_saves_to_the_group_at_the_same_instant_the_loser_gets_409()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l.Group.Id);
        app.BeforeNextSave.Arm(async () =>
        {
            await using var session = Store.LightweightSession();
            session.Events.Append(l.Group.Id.Value, new SplitIt.Slices.CreateGroup.MemberAdded(MemberId.New(), "Dave", _alice));
            await session.SaveChangesAsync();
        });

        var response = await Submit(browser, form, l, description: "Dinner", amount: "90");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    // ── Who may ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task S15_a_non_member_posting_is_404_and_records_nothing()
    {
        var l = await SeedLisbon();
        var mallory = app.BrowserFor(UserId.New());
        var mallorysToken = await Forms.TokenFrom(mallory, "/groups/new");

        var response = await Forms.Post(mallory, $"/groups/{l.Group.Id}/expenses", mallorysToken,
            ("expenseId", ExpenseId.New().ToString()), ("mode", "equal"), ("description", "Dinner"), ("amount", "90"),
            ("payer", l.Alice.ToString()), ("participants", l.Alice.ToString()), ("paidOn", Today));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    [Fact]
    public async Task Without_a_valid_antiforgery_token_it_is_400_and_records_nothing()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        await FormPage(browser, l.Group.Id);

        var response = await Forms.Post(browser, $"/groups/{l.Group.Id}/expenses", "forged",
            ("expenseId", ExpenseId.New().ToString()), ("mode", "equal"), ("description", "Dinner"), ("amount", "90"),
            ("payer", l.Alice.ToString()), ("participants", l.Alice.ToString()), ("paidOn", Today));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static async Task<string> FormPage(HttpClient browser, GroupId group)
    {
        var response = await browser.GetAsync($"/groups/{group}/expenses/new");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Submits <paramref name="form"/> as shown — its token and expense id — with these values; what is left out is what the form offered.</summary>
    private Task<HttpResponseMessage> Submit(
        HttpClient browser, string form, Lisbon l, string description, string amount, MemberId? payer = null,
        MemberId[]? participants = null, string? paidOn = null, string mode = "equal") =>
        Forms.Post(browser, $"/groups/{l.Group.Id}/expenses", Forms.TokenIn(form)!,
        [
            ("expenseId", ExpenseIdIn(form).ToString()),
            ("mode", mode),
            ("description", description),
            ("amount", amount),
            ("payer", (payer ?? l.Alice).ToString()),
            .. (participants ?? [l.Alice, l.Bob, l.Carol]).Select(p => ("participants", p.ToString())),
            ("paidOn", paidOn ?? Today),
        ]);

    private static ExpenseId ExpenseIdIn(string html) =>
        ExpenseId.From(Guid.Parse(HiddenExpenseId().Match(html) is { Success: true } m ? m.Groups[1].Value
            : throw new InvalidOperationException("No expense id in the form")));

    private async Task<IReadOnlyList<ExpenseRecorded>> ExpensesOf(GroupId group)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(group.Value)).Select(e => e.Data).OfType<ExpenseRecorded>().ToList();
    }

    [GeneratedRegex("""<input type="hidden" name="expenseId" value="([^"]+)" />""")]
    private static partial Regex HiddenExpenseId();
}
