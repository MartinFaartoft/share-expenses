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
        Assert.Contains($"""<a class="up" href="/groups/{l.Group.Id}" aria-label="Back">‹</a>""", html);
        Assert.Contains($"""<form method="post" action="/groups/{l.Group.Id}/expenses">""", html);
        Assert.Contains("""<input type="radio" name="mode" value="equal" checked />""", html);
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

    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    public async Task A_mode_the_form_does_not_know_is_a_rejection_not_a_server_error(string mode)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l.Group.Id);

        var response = await Submit(browser, form, l, description: "Dinner", amount: "90", mode: mode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("split mode not supported", html);
        Assert.Contains("""<input type="radio" name="mode" value="equal" checked />""", html);
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    // ── Shares and exact ──────────────────────────────────────────────────────────

    private static (string Name, string Value) Shares(MemberId member, string value) => ($"shares-{member}", value);

    private static (string Name, string Value) Owes(MemberId member, string value) => ($"amount-{member}", value);

    [Fact]
    public async Task The_form_has_every_modes_fields_for_every_member_with_equally_selected()
    {
        var l = await SeedLisbon();

        var html = await FormPage(app.BrowserFor(_alice), l.Group.Id);

        Assert.Contains("""<input type="radio" name="mode" value="equal" checked />""", html);
        Assert.Contains("""<input type="radio" name="mode" value="shares" />""", html);
        Assert.Contains("""<input type="radio" name="mode" value="exact" />""", html);
        foreach (var member in new[] { l.Alice, l.Bob, l.Carol })
        {
            Assert.Contains($"""name="shares-{member}" value="1" """, html);
            Assert.Contains($"""name="amount-{member}" value="" """, html);
        }
        Assert.Contains("""data-places="2" data-prefix="£" """, html);
    }

    [Fact]
    public async Task S7_records_a_shares_split()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l.Group.Id);

        var response = await Submit(browser, form, l, description: "Flat", amount: "10.00", mode: "shares",
            extra: [Shares(l.Alice, "2"), Shares(l.Bob, "1"), Shares(l.Carol, "1")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var recorded = Assert.Single(await ExpensesOf(l.Group.Id));
        Assert.Equal(new SharesSplit([new MemberShares(l.Alice, 2), new MemberShares(l.Bob, 1), new MemberShares(l.Carol, 1)]), recorded.Split);
        Assert.Equal([new Split(l.Alice, 500), new Split(l.Bob, 250), new Split(l.Carol, 250)], recorded.Splits);
    }

    [Fact]
    public async Task S8_a_shares_split_gives_the_leftover_by_largest_remainder()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Flat", amount: "1.00", mode: "shares",
            participants: [l.Alice, l.Bob], extra: [Shares(l.Alice, "1"), Shares(l.Bob, "2")]);

        Assert.Equal([new Split(l.Alice, 33), new Split(l.Bob, 67)], Assert.Single(await ExpensesOf(l.Group.Id)).Splits);
    }

    [Fact]
    public async Task The_shares_of_a_member_who_is_not_checked_are_ignored_even_when_unreadable()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Flat", amount: "9.00", mode: "shares",
            participants: [l.Alice, l.Bob], extra: [Shares(l.Alice, "1"), Shares(l.Bob, "2"), Shares(l.Carol, "abc")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var recorded = Assert.Single(await ExpensesOf(l.Group.Id));
        Assert.Equal(new SharesSplit([new MemberShares(l.Alice, 1), new MemberShares(l.Bob, 2)]), recorded.Split);
        Assert.Equal([new Split(l.Alice, 300), new Split(l.Bob, 600)], recorded.Splits);
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("shares")]
    [InlineData("exact")]
    public async Task The_fields_of_the_modes_not_selected_are_ignored_even_when_unreadable(string mode)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        (string, string)[] own = mode switch
        {
            "shares" => [Shares(l.Alice, "1"), Shares(l.Bob, "1"), Shares(l.Carol, "1")],
            "exact" => [Owes(l.Alice, "30"), Owes(l.Bob, "30"), Owes(l.Carol, "30")],
            _ => [],
        };
        (string, string)[] others =
        [
            .. mode == "shares" ? [] : new[] { Shares(l.Alice, "x"), Shares(l.Bob, "-2"), Shares(l.Carol, "") },
            .. mode == "exact" ? [] : new[] { Owes(l.Alice, "x"), Owes(l.Bob, "1.234"), Owes(l.Carol, "") },
        ];

        var response = await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Dinner", amount: "90", mode: mode,
            extra: [.. own, .. others]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(9000, Assert.Single(await ExpensesOf(l.Group.Id)).AmountMinor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1,5")]
    [InlineData("2 shares")]
    [InlineData("99999999999")]
    public async Task A_share_that_is_not_a_whole_number_is_shown_back_as_typed_and_nothing_is_recorded(string typed)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Flat", amount: "10", mode: "shares",
            extra: [Shares(l.Alice, "2"), Shares(l.Bob, typed), Shares(l.Carol, "1")]);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("""<p class="error" role="alert">every share must be a whole number</p>""", html);
        Assert.Contains("""<input type="radio" name="mode" value="shares" checked />""", html);
        Assert.Contains($"""name="shares-{l.Alice}" value="2" """, html);
        Assert.Contains($"""name="shares-{l.Bob}" value="{typed}" """, html);
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task S25_a_share_that_is_not_positive_is_rejected_by_deciding(string typed)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Flat", amount: "10", mode: "shares",
            extra: [Shares(l.Alice, "2"), Shares(l.Bob, typed), Shares(l.Carol, "1")]);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("every share must be a positive whole number", html);
        Assert.Contains($"""name="shares-{l.Bob}" value="{typed}" """, html);
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    [Fact]
    public async Task A_checked_member_with_no_shares_field_at_all_is_a_shape_error()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Flat", amount: "10", mode: "shares",
            extra: [Shares(l.Alice, "1"), Shares(l.Bob, "1")]);

        Assert.Contains("every share must be a whole number", await response.Content.ReadAsStringAsync());
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    [Theory]
    [InlineData("shares")]
    [InlineData("exact")]
    public async Task S23_nobody_checked_is_rejected_by_deciding_in_every_mode(string mode)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Flat", amount: "10", mode: mode,
            participants: []);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains("at least one participant is required", html);
        Assert.Contains($"""<input type="radio" name="mode" value="{mode}" checked />""", html);
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    [Fact]
    public async Task S10_records_an_exact_split_and_the_payer_need_not_share()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Steak night", amount: "50", mode: "exact",
            payer: l.Carol, participants: [l.Alice, l.Bob], extra: [Owes(l.Alice, "20"), Owes(l.Bob, "30.00"), Owes(l.Carol, "garbage")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var recorded = Assert.Single(await ExpensesOf(l.Group.Id));
        Assert.Equal(new ExactSplit([new MemberAmount(l.Alice, 2000), new MemberAmount(l.Bob, 3000)]), recorded.Split);
        Assert.Equal([new Split(l.Alice, 2000), new Split(l.Bob, 3000)], recorded.Splits);
        Assert.Equal(l.Carol, recorded.PayerMemberId);
    }

    [Theory]
    [InlineData("12.50", "12,50")]
    [InlineData("12,5", "12.5")]
    [InlineData(" 12.50 ", "12.50")]
    public async Task Exact_amounts_are_typed_in_the_currencys_units_like_the_amount(string first, string second)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Lunch", amount: "25", mode: "exact",
            participants: [l.Alice, l.Bob], extra: [Owes(l.Alice, first), Owes(l.Bob, second)]);

        Assert.Equal([new Split(l.Alice, 1250), new Split(l.Bob, 1250)], Assert.Single(await ExpensesOf(l.Group.Id)).Splits);
    }

    [Fact]
    public async Task A_yen_exact_amount_has_no_decimals()
    {
        var l = await SeedLisbon("JPY");
        var browser = app.BrowserFor(_alice);

        var rejected = await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Lunch", amount: "1000", mode: "exact",
            participants: [l.Alice, l.Bob], extra: [Owes(l.Alice, "400.5"), Owes(l.Bob, "600")]);
        await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Lunch", amount: "1000", mode: "exact",
            participants: [l.Alice, l.Bob], extra: [Owes(l.Alice, "400"), Owes(l.Bob, "600")]);

        Assert.Contains("every exact amount must be a number", await rejected.Content.ReadAsStringAsync());
        Assert.Equal([new Split(l.Alice, 400), new Split(l.Bob, 600)], Assert.Single(await ExpensesOf(l.Group.Id)).Splits);
    }

    [Fact]
    public async Task A_zero_exact_amount_is_allowed()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Steak night", amount: "50", mode: "exact",
            participants: [l.Alice, l.Bob], extra: [Owes(l.Alice, "50"), Owes(l.Bob, "0")]);

        Assert.Equal([new Split(l.Alice, 5000), new Split(l.Bob, 0)], Assert.Single(await ExpensesOf(l.Group.Id)).Splits);
    }

    [Fact]
    public async Task S11_exact_amounts_that_do_not_add_up_are_rejected_and_shown_back_as_typed()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Steak night", amount: "50", mode: "exact",
            participants: [l.Alice, l.Bob], extra: [Owes(l.Alice, "20"), Owes(l.Bob, "29.99")]);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("exact amounts must add up to the total", html);
        Assert.Contains("""<input type="radio" name="mode" value="exact" checked />""", html);
        Assert.Contains($"""name="amount-{l.Alice}" value="20" """, html);
        Assert.Contains($"""name="amount-{l.Bob}" value="29.99" """, html);
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.234")]
    [InlineData("-5")]
    [InlineData("1,234.50")]
    public async Task An_exact_amount_that_is_not_an_amount_is_shown_back_as_typed_and_nothing_is_recorded(string typed)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Steak night", amount: "50", mode: "exact",
            participants: [l.Alice, l.Bob], extra: [Owes(l.Alice, "20"), Owes(l.Bob, typed)]);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("""<p class="error" role="alert">every exact amount must be a number</p>""", html);
        Assert.Contains($"""name="amount-{l.Bob}" value="{typed}" """, html);
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    [Fact]
    public async Task A_negative_exact_amount_is_unreadable_not_a_server_error()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Steak night", amount: "50", mode: "exact",
            participants: [l.Alice, l.Bob], extra: [Owes(l.Alice, "60"), Owes(l.Bob, "-10")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ExpensesOf(l.Group.Id));
    }

    [Fact]
    public async Task A_shape_error_hides_nothing_about_the_rest_of_the_form()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Steak night", amount: "50", mode: "exact",
            payer: l.Bob, participants: [l.Alice, l.Carol], paidOn: "2026-06-15",
            extra: [Owes(l.Alice, "20"), Owes(l.Carol, "oops"), Shares(l.Alice, "3"), Shares(l.Bob, "4")]);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains("""name="description" value="Steak night" """, html);
        Assert.Contains("""name="amount" value="50" """, html);
        Assert.Contains($"""<option value="{l.Bob}" selected>Bob</option>""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Alice}" checked />""", html);
        Assert.DoesNotContain($"""<input type="checkbox" name="participants" value="{l.Bob}" checked />""", html);
        Assert.Contains($"""name="shares-{l.Alice}" value="3" """, html);
        Assert.Contains($"""name="shares-{l.Bob}" value="4" """, html);
        Assert.Contains($"""name="amount-{l.Carol}" value="oops" """, html);
        Assert.Contains("""name="paidOn" value="2026-06-15" """, html);
    }

    [Fact]
    public async Task A_rejected_equal_form_keeps_what_was_typed_in_the_other_modes()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "", amount: "50", mode: "equal",
            extra: [Shares(l.Bob, "5"), Owes(l.Bob, "12.34")]);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains("description is required", html);
        Assert.Contains("""<input type="radio" name="mode" value="equal" checked />""", html);
        Assert.Contains($"""name="shares-{l.Bob}" value="5" """, html);
        Assert.Contains($"""name="amount-{l.Bob}" value="12.34" """, html);
    }

    [Fact]
    public async Task A_shape_error_never_reaches_the_group_stream_as_a_half_made_expense()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l.Group.Id);

        await Submit(browser, form, l, description: "Flat", amount: "10", mode: "shares", extra: [Shares(l.Alice, "x")]);
        var fixedUp = await Submit(browser, form, l, description: "Flat", amount: "10", mode: "shares",
            extra: [Shares(l.Alice, "1"), Shares(l.Bob, "1"), Shares(l.Carol, "1")]);

        Assert.Equal(HttpStatusCode.Redirect, fixedUp.StatusCode);
        Assert.Single(await ExpensesOf(l.Group.Id));
    }

    // ── The group's default split (slice-16) ─────────────────────────────────────

    [Fact]
    public async Task The_form_opens_with_the_groups_equal_default_and_the_left_out_unchecked()
    {
        var l = await SeedLisbon();
        await l.Group.DefaultSplit("equal", (l.Carol, 0));

        var html = await FormPage(app.BrowserFor(_alice), l.Group.Id);

        Assert.Contains("""<input type="radio" name="mode" value="equal" checked />""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Alice}" checked />""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Bob}" checked />""", html);
        Assert.DoesNotContain($"""<input type="checkbox" name="participants" value="{l.Carol}" checked />""", html);
    }

    [Fact]
    public async Task The_form_opens_with_the_groups_shares_default_mode_and_weights()
    {
        var l = await SeedLisbon();
        await l.Group.DefaultSplit("shares", (l.Alice, 2), (l.Carol, 0));

        var html = await FormPage(app.BrowserFor(_alice), l.Group.Id);

        Assert.Contains("""<input type="radio" name="mode" value="shares" checked />""", html);
        Assert.Contains($"""name="shares-{l.Alice}" value="2" """, html);
        Assert.Contains($"""name="shares-{l.Bob}" value="1" """, html);
        Assert.Contains($"""name="shares-{l.Carol}" value="1" """, html);
        Assert.DoesNotContain($"""<input type="checkbox" name="participants" value="{l.Carol}" checked />""", html);
        Assert.Contains($"""name="amount-{l.Alice}" value="" """, html);
    }

    [Fact]
    public async Task A_member_added_after_the_default_starts_in_it()
    {
        var l = await SeedLisbon();
        await l.Group.DefaultSplit("shares", (l.Alice, 2), (l.Carol, 0));
        var dave = await l.Group.Member("Dave");

        var html = await FormPage(app.BrowserFor(_alice), l.Group.Id);

        Assert.Contains($"""<input type="checkbox" name="participants" value="{dave}" checked />""", html);
        Assert.Contains($"""name="shares-{dave}" value="1" """, html);
    }

    [Fact]
    public async Task The_latest_default_is_the_one_the_form_opens_with()
    {
        var l = await SeedLisbon();
        await l.Group.DefaultSplit("shares", (l.Alice, 2));
        await l.Group.DefaultSplit("equal");

        var html = await FormPage(app.BrowserFor(_alice), l.Group.Id);

        Assert.Contains("""<input type="radio" name="mode" value="equal" checked />""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Carol}" checked />""", html);
    }

    [Fact]
    public async Task The_form_posted_as_it_opened_records_the_default_split()
    {
        var l = await SeedLisbon();
        await l.Group.DefaultSplit("shares", (l.Alice, 2), (l.Carol, 0));
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l.Group.Id);

        await Submit(browser, form, l, description: "Flat", amount: "9.00", mode: "shares",
            participants: [l.Alice, l.Bob], extra: [Shares(l.Alice, "2"), Shares(l.Bob, "1"), Shares(l.Carol, "1")]);

        var recorded = Assert.Single(await ExpensesOf(l.Group.Id));
        Assert.Equal(new SharesSplit([new MemberShares(l.Alice, 2), new MemberShares(l.Bob, 1)]), recorded.Split);
        Assert.Equal([new Split(l.Alice, 600), new Split(l.Bob, 300)], recorded.Splits);
    }

    [Fact]
    public async Task A_default_is_not_a_rule_anyone_can_be_added_to_an_expense_by_checking_them()
    {
        var l = await SeedLisbon();
        await l.Group.DefaultSplit("equal", (l.Carol, 0));
        var browser = app.BrowserFor(_alice);

        await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "Dinner", amount: "90");

        Assert.Equal(new EqualSplit([l.Alice, l.Bob, l.Carol]), Assert.Single(await ExpensesOf(l.Group.Id)).Split);
    }

    [Fact]
    public async Task A_rejected_form_shows_what_was_typed_not_the_default()
    {
        var l = await SeedLisbon();
        await l.Group.DefaultSplit("shares", (l.Alice, 2), (l.Carol, 0));
        var browser = app.BrowserFor(_alice);

        var response = await Submit(browser, await FormPage(browser, l.Group.Id), l, description: "", amount: "9", mode: "equal");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains("""<input type="radio" name="mode" value="equal" checked />""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Carol}" checked />""", html);
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
        MemberId[]? participants = null, string? paidOn = null, string mode = "equal",
        params (string Name, string Value)[] extra) =>
        Forms.Post(browser, $"/groups/{l.Group.Id}/expenses", Forms.TokenIn(form)!,
        [
            ("expenseId", ExpenseIdIn(form).ToString()),
            ("mode", mode),
            ("description", description),
            ("amount", amount),
            ("payer", (payer ?? l.Alice).ToString()),
            .. (participants ?? [l.Alice, l.Bob, l.Carol]).Select(p => ("participants", p.ToString())),
            ("paidOn", paidOn ?? Today),
            .. extra,
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
