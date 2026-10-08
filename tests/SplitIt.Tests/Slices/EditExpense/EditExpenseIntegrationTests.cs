using System.Net;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SplitIt.Shared;
using SplitIt.Slices.EditExpense;
using SplitIt.Tests.Infrastructure;
using ExpenseRecorded = SplitIt.Slices.RecordExpense.ExpenseRecorded;

namespace SplitIt.Tests.Slices.EditExpense;

/// <summary>
/// The Edit expense screen (slice-12-edit-expense.md), as a browser uses it: open the
/// form, post it with its token. Mostly the post and what deciding makes of it; the
/// page itself is only checked for being filled in.
/// </summary>
[Collection(AppCollection.Name)]
public class EditExpenseIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();
    private readonly UserId _bob = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    private sealed record Lisbon(SeededGroup Group, MemberId Alice, MemberId Bob, MemberId Carol, ExpenseId Dinner);

    /// <summary>Dinner, £90.00 paid by Alice on 1 October and shared by all three.</summary>
    private async Task<Lisbon> SeedLisbon(string currency = "GBP")
    {
        var group = await new Seed(app).Group(_alice, "Lisbon trip", currency);
        var bob = await group.Joined("Bob", _bob);
        var carol = await group.Member("Carol");
        var dinner = await group.Expense("Dinner", 9000, group.You, [group.You, bob, carol]);
        return new Lisbon(group, group.You, bob, carol, dinner);
    }

    private string Today => app.Clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd");

    private static string Path(Lisbon l) => Path(l.Group.Id, l.Dinner);

    private static string Path(GroupId group, ExpenseId expense) => $"/groups/{group}/expenses/{expense}/edit";

    // ── The form ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_form_is_filled_in_with_the_expense_as_it_stands()
    {
        var l = await SeedLisbon();
        await l.Group.Seed.Append(l.Group.Id, new ExpenseEdited(
            l.Dinner, "Team dinner", 12050, l.Bob, new EqualSplit([l.Alice, l.Carol]),
            [new Split(l.Alice, 6025), new Split(l.Carol, 6025)], new DateOnly(2026, 9, 28), _bob));

        var html = await FormPage(app.BrowserFor(_alice), l);

        Assert.Contains("<title>Edit expense · SplitIt</title>", html);
        Assert.Contains($"""<form method="post" action="{Path(l)}">""", html);
        Assert.Contains("""name="description" value="Team dinner" """, html);
        Assert.Contains("""name="amount" value="120.50" """, html);
        Assert.Contains($"""<option value="{l.Bob}" selected>Bob</option>""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Alice}" checked />""", html);
        Assert.DoesNotContain($"""<input type="checkbox" name="participants" value="{l.Bob}" checked />""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Carol}" checked />""", html);
        Assert.Contains("""name="paidOn" value="2026-09-28" """, html);
        Assert.NotNull(Forms.TokenIn(html));
    }

    [Theory]
    [InlineData("GBP", 12050, "120.50")]
    [InlineData("GBP", 100000, "1000.00")]
    [InlineData("GBP", 5, "0.05")]
    [InlineData("JPY", 1200, "1200")]
    [InlineData("KWD", 1234567, "1234.567")]
    public async Task The_amount_is_shown_as_it_would_be_typed_so_posting_it_back_changes_nothing(string currency, long minor, string typed)
    {
        var l = await SeedLisbon(currency);
        await l.Group.Seed.Append(l.Group.Id, new ExpenseEdited(
            l.Dinner, "Dinner", minor, l.Alice, new EqualSplit([l.Alice]), [new Split(l.Alice, minor)], new DateOnly(2026, 10, 1), _alice));
        var browser = app.BrowserFor(_alice);

        var form = await FormPage(browser, l);
        var response = await Post(browser, l, form, Fields(l, amount: typed, participants: [l.Alice], paidOn: "2026-10-01"));

        Assert.Contains($"""name="amount" value="{typed}" """, form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Single(await EditsOf(l.Group.Id));
    }

    [Fact]
    public async Task Whoever_cannot_edit_it_gets_the_same_404_on_the_form()
    {
        var l = await SeedLisbon();
        var removed = await l.Group.Expense("Taxi", 3000, l.Alice, [l.Alice]);
        await l.Group.RemoveExpense(removed);
        var mallory = app.BrowserFor(UserId.New());
        var alice = app.BrowserFor(_alice);

        var responses = new[]
        {
            await mallory.GetAsync(Path(l)),
            await mallory.GetAsync(Path(GroupId.New(), l.Dinner)),
            await mallory.GetAsync($"/groups/nonsense/expenses/{l.Dinner}/edit"),
            await alice.GetAsync(Path(l.Group.Id, ExpenseId.New())),
            await alice.GetAsync(Path(l.Group.Id, removed)),
            await alice.GetAsync($"/groups/{l.Group.Id}/expenses/nonsense/edit"),
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
        Assert.Equal($"/sign-in?returnUrl=%2Fgroups%2F{l.Group.Id}%2Fexpenses%2F{l.Dinner}%2Fedit", response.Headers.Location?.OriginalString);
    }

    // ── Saving ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task S1_saves_a_new_amount_with_recomputed_splits_and_goes_back_to_the_group()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l);

        var response = await Post(browser, l, form, Fields(l, amount: "120.00"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{l.Group.Id}", response.Headers.Location?.OriginalString);
        var edited = Assert.Single(await EditsOf(l.Group.Id));
        Assert.Equal((l.Dinner, "Dinner", 12000L, l.Alice, _alice), (edited.ExpenseId, edited.Description, edited.AmountMinor, edited.PayerMemberId, edited.By));
        Assert.Equal(new EqualSplit([l.Alice, l.Bob, l.Carol]), edited.Split);
        Assert.Equal([new Split(l.Alice, 4000), new Split(l.Bob, 4000), new Split(l.Carol, 4000)], edited.Splits);
        Assert.Equal(new DateOnly(2026, 10, 1), edited.PaidOn);
    }

    [Fact]
    public async Task Every_field_can_change_at_once()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l);

        await Post(browser, l, form, Fields(l, description: "  Taxi ", amount: "30", payer: l.Carol,
            participants: [l.Carol, l.Bob], paidOn: "2026-10-02"));

        var edited = Assert.Single(await EditsOf(l.Group.Id));
        Assert.Equal(("Taxi", 3000L, l.Carol, new DateOnly(2026, 10, 2)), (edited.Description, edited.AmountMinor, edited.PayerMemberId, edited.PaidOn));
        Assert.Equal(new EqualSplit([l.Bob, l.Carol]), edited.Split);
        Assert.Equal([new Split(l.Bob, 1500), new Split(l.Carol, 1500)], edited.Splits);
    }

    [Fact]
    public async Task A_new_payer_moves_the_rounding_leftover()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l);

        await Post(browser, l, form, Fields(l, amount: "10.00", payer: l.Bob));

        Assert.Equal([new Split(l.Alice, 333), new Split(l.Bob, 334), new Split(l.Carol, 333)], Assert.Single(await EditsOf(l.Group.Id)).Splits);
    }

    [Theory]
    [InlineData("120.50", 12050)]
    [InlineData("120,50", 12050)]
    [InlineData("120", 12000)]
    [InlineData("0.01", 1)]
    [InlineData(" 120.5 ", 12050)]
    public async Task The_amount_is_typed_in_the_currencys_units(string typed, long minor)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        await Post(browser, l, await FormPage(browser, l), Fields(l, amount: typed));

        Assert.Equal(minor, Assert.Single(await EditsOf(l.Group.Id)).AmountMinor);
    }

    [Fact]
    public async Task A_yen_amount_has_no_decimals()
    {
        var l = await SeedLisbon("JPY");
        var browser = app.BrowserFor(_alice);

        var rejected = await Post(browser, l, await FormPage(browser, l), Fields(l, amount: "1200.5"));
        await Post(browser, l, await FormPage(browser, l), Fields(l, amount: "1200"));

        Assert.Contains("amount must be a number", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(1200, Assert.Single(await EditsOf(l.Group.Id)).AmountMinor);
    }

    [Fact]
    public async Task Any_member_may_edit_it_and_is_recorded_as_the_actor()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_bob);

        await Post(browser, l, await FormPage(browser, l), Fields(l, amount: "120"));

        Assert.Equal(_bob, Assert.Single(await EditsOf(l.Group.Id)).By);
    }

    [Fact]
    public async Task A_form_posted_as_shown_changes_nothing_and_goes_back_to_the_group()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), Fields(l));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{l.Group.Id}", response.Headers.Location?.OriginalString);
        Assert.Empty(await EditsOf(l.Group.Id));
    }

    [Fact]
    public async Task Submitting_twice_edits_once_and_both_go_back_to_the_group()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l);

        var first = await Post(browser, l, form, Fields(l, amount: "120"));
        var second = await Post(browser, l, form, Fields(l, amount: "120"));

        Assert.Equal($"/groups/{l.Group.Id}", first.Headers.Location?.OriginalString);
        Assert.Equal($"/groups/{l.Group.Id}", second.Headers.Location?.OriginalString);
        Assert.Single(await EditsOf(l.Group.Id));
    }

    [Fact]
    public async Task S11_last_write_wins_a_form_opened_before_another_edit_still_saves()
    {
        var l = await SeedLisbon();
        var alice = app.BrowserFor(_alice);
        var bob = app.BrowserFor(_bob);
        var aliceForm = await FormPage(alice, l);
        var bobForm = await FormPage(bob, l);

        await Post(bob, l, bobForm, Fields(l, amount: "100"));
        var response = await Post(alice, l, aliceForm, Fields(l, description: "Team dinner", amount: "120"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var edits = await EditsOf(l.Group.Id);
        Assert.Equal([(_bob, 10000L), (_alice, 12000L)], edits.Select(e => (e.By, e.AmountMinor)));
        Assert.Equal("Team dinner", edits[^1].Description);
    }

    [Fact]
    public async Task The_edit_shows_on_the_group_page_where_the_expense_was()
    {
        var l = await SeedLisbon();
        var lunch = await l.Group.Expense("Lunch", 1000, l.Alice, [l.Alice, l.Bob], new DateOnly(2026, 10, 1));
        var browser = app.BrowserFor(_alice);

        await Post(browser, l, await FormPage(browser, l), Fields(l, description: "Team dinner", amount: "120"));
        var page = WebUtility.HtmlDecode(await (await browser.GetAsync($"/groups/{l.Group.Id}")).Content.ReadAsStringAsync());

        Assert.Contains("Team dinner", page);
        Assert.DoesNotContain(">Dinner<", page);
        Assert.True(page.IndexOf("Lunch", StringComparison.Ordinal) < page.IndexOf("Team dinner", StringComparison.Ordinal),
            "newest first: the edited expense keeps its place, recorded before the lunch, so it is listed after it");
    }

    // ── Rejected: the form again, with what was entered ──────────────────────────

    [Theory]
    [InlineData("description", "   ", "description is required")]
    [InlineData("description", "", "description is required")]
    [InlineData("amount", "abc", "amount must be a number")]
    [InlineData("amount", "", "amount must be a number")]
    [InlineData("amount", "12.345", "amount must be a number")]
    [InlineData("amount", "1,234.50", "amount must be a number")]
    [InlineData("amount", "-5", "amount must be a number")]
    [InlineData("amount", "0", "amount must be positive")]
    [InlineData("amount", "0.00", "amount must be positive")]
    [InlineData("amount", "10000000000.01", "amount is too large")]
    [InlineData("paidOn", "yesterday", "date is required")]
    [InlineData("paidOn", "", "date is required")]
    [InlineData("paidOn", "01/10/2026", "date is required")]
    public async Task A_field_deciding_refuses_is_shown_back_as_typed_with_the_reason_and_nothing_is_saved(string field, string value, string reason)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l);

        var response = await Post(browser, l, form, With(Fields(l), field, value));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"""<p class="error" role="alert">{reason}</p>""", html);
        Assert.Contains($"""name="{field}" value="{value}" """, html);
        Assert.Empty(await EditsOf(l.Group.Id));
    }

    [Fact]
    public async Task A_description_over_100_characters_is_rejected_and_100_is_not()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var tooLong = await Post(browser, l, await FormPage(browser, l), Fields(l, description: new string('x', 101)));
        var longest = await Post(browser, l, await FormPage(browser, l), Fields(l, description: new string('x', 100)));

        Assert.Contains("description must be at most 100 characters", await tooLong.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, longest.StatusCode);
        Assert.Equal(new string('x', 100), Assert.Single(await EditsOf(l.Group.Id)).Description);
    }

    [Fact]
    public async Task S36_a_date_beyond_tomorrow_is_rejected_and_tomorrow_is_not()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var tomorrow = DateOnly.Parse(Today).AddDays(1).ToString("yyyy-MM-dd");
        var later = DateOnly.Parse(Today).AddDays(2).ToString("yyyy-MM-dd");

        var tooLate = await Post(browser, l, await FormPage(browser, l), Fields(l, paidOn: later));
        var ok = await Post(browser, l, await FormPage(browser, l), Fields(l, paidOn: tomorrow));

        Assert.Contains("date cannot be in the future", await tooLate.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal(DateOnly.Parse(tomorrow), Assert.Single(await EditsOf(l.Group.Id)).PaidOn);
    }

    [Fact]
    public async Task A_payer_that_is_not_a_slot_of_the_group_is_rejected()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l);

        var forged = await Post(browser, l, form, With(Fields(l), "payer", MemberId.New().ToString()));
        var garbled = await Post(browser, l, form, With(Fields(l), "payer", "not-a-guid"));
        var missing = await Post(browser, l, form, Fields(l).Where(f => f.Name != "payer").ToArray());

        foreach (var response in new[] { forged, garbled, missing })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("payer is not a member of the group", await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(await EditsOf(l.Group.Id));
    }

    [Fact]
    public async Task S29_nobody_sharing_is_rejected_by_deciding_not_prevented_by_the_page()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), Fields(l, participants: []));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("at least one participant is required", html);
        Assert.DoesNotMatch(@"type=""checkbox""[^>]*checked", html);
        Assert.Empty(await EditsOf(l.Group.Id));
    }

    [Fact]
    public async Task S28_a_participant_that_is_not_a_slot_of_the_group_is_rejected_and_unreadable_ones_are_ignored()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l);

        var forged = await Post(browser, l, form, [.. Fields(l), ("participants", MemberId.New().ToString())]);
        var garbled = await Post(browser, l, form, [.. Fields(l, participants: [l.Alice]), ("participants", "not-a-guid")]);

        Assert.Contains("participant is not a member of the group", await forged.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, garbled.StatusCode);
        Assert.Equal(new EqualSplit([l.Alice]), Assert.Single(await EditsOf(l.Group.Id)).Split);
    }

    [Fact]
    public async Task A_rejected_form_shows_back_every_field_as_entered()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l),
            Fields(l, description: "  ", amount: "12.5", payer: l.Bob, participants: [l.Alice, l.Carol], paidOn: "2026-06-15"));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains("description is required", html);
        Assert.Contains("""name="amount" value="12.5" """, html);
        Assert.Contains($"""<option value="{l.Bob}" selected>Bob</option>""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Alice}" checked />""", html);
        Assert.DoesNotContain($"""<input type="checkbox" name="participants" value="{l.Bob}" checked />""", html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Carol}" checked />""", html);
        Assert.Contains("""name="paidOn" value="2026-06-15" """, html);
        Assert.Contains($"""action="{Path(l)}" """.TrimEnd(), html);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    public async Task A_mode_the_form_does_not_know_is_a_rejection_not_a_server_error(string mode)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), With(Fields(l, amount: "120"), "mode", mode));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("split mode not supported", html);
        Assert.Contains("""<input type="radio" name="mode" value="equal" checked />""", html);
        Assert.Empty(await EditsOf(l.Group.Id));
    }

    [Fact]
    public async Task A_rejected_edit_leaves_an_earlier_edit_standing()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        await Post(browser, l, await FormPage(browser, l), Fields(l, amount: "120"));

        await Post(browser, l, await FormPage(browser, l), Fields(l, amount: "0"));

        Assert.Equal(12000, Assert.Single(await EditsOf(l.Group.Id)).AmountMinor);
        Assert.Contains("""name="amount" value="120.00" """, await FormPage(browser, l));
    }

    // ── Shares and exact ──────────────────────────────────────────────────────────

    private static (string Name, string Value) Shares(MemberId member, string value) => ($"shares-{member}", value);

    private static (string Name, string Value) Owes(MemberId member, string value) => ($"amount-{member}", value);

    /// <summary>Flat, £10.00 paid by Alice, shared by shares: Alice 2, Bob 1, Carol 1.</summary>
    private async Task<ExpenseId> SeedFlat(Lisbon l)
    {
        var flat = ExpenseId.New();
        await l.Group.Seed.Append(l.Group.Id, new ExpenseRecorded(flat, "Flat", 1000, l.Alice,
            new SharesSplit([new MemberShares(l.Alice, 2), new MemberShares(l.Bob, 1), new MemberShares(l.Carol, 1)]),
            [new Split(l.Alice, 500), new Split(l.Bob, 250), new Split(l.Carol, 250)], new DateOnly(2026, 10, 1), _alice));
        return flat;
    }

    /// <summary>Steak night, £50.00 paid by Carol: Alice £20.00, Bob £30.00, nothing for Carol.</summary>
    private async Task<ExpenseId> SeedSteak(Lisbon l)
    {
        var steak = ExpenseId.New();
        await l.Group.Seed.Append(l.Group.Id, new ExpenseRecorded(steak, "Steak night", 5000, l.Carol,
            new ExactSplit([new MemberAmount(l.Alice, 2000), new MemberAmount(l.Bob, 3000)]),
            [new Split(l.Alice, 2000), new Split(l.Bob, 3000)], new DateOnly(2026, 10, 1), _alice));
        return steak;
    }

    private static string Path(Lisbon l, ExpenseId expense) => Path(l.Group.Id, expense);

    private static async Task<string> FormPageOf(HttpClient browser, Lisbon l, ExpenseId expense)
    {
        var response = await browser.GetAsync(Path(l, expense));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> PostTo(HttpClient browser, Lisbon l, ExpenseId expense, string form, params (string Name, string Value)[] fields) =>
        Forms.Post(browser, Path(l, expense), Forms.TokenIn(form)!, fields);

    [Fact]
    public async Task The_form_of_an_equal_expense_starts_the_other_modes_from_where_it_is()
    {
        var l = await SeedLisbon();

        var html = await FormPage(app.BrowserFor(_alice), l);

        Assert.Contains("""<input type="radio" name="mode" value="equal" checked />""", html);
        foreach (var member in new[] { l.Alice, l.Bob, l.Carol })
        {
            Assert.Contains($"""name="shares-{member}" value="1" """, html);
            Assert.Contains($"""name="amount-{member}" value="30.00" """, html);
        }
    }

    [Fact]
    public async Task The_form_of_a_shares_expense_has_its_mode_and_weights()
    {
        var l = await SeedLisbon();
        var flat = await SeedFlat(l);

        var html = await FormPageOf(app.BrowserFor(_alice), l, flat);

        Assert.Contains("""<input type="radio" name="mode" value="shares" checked />""", html);
        Assert.Contains($"""name="shares-{l.Alice}" value="2" """, html);
        Assert.Contains($"""name="shares-{l.Bob}" value="1" """, html);
        Assert.Contains($"""name="amount-{l.Alice}" value="5.00" """, html);
    }

    [Fact]
    public async Task The_form_of_an_exact_expense_has_its_mode_amounts_and_members()
    {
        var l = await SeedLisbon();
        var steak = await SeedSteak(l);

        var html = await FormPageOf(app.BrowserFor(_alice), l, steak);

        Assert.Contains("""<input type="radio" name="mode" value="exact" checked />""", html);
        Assert.Contains($"""name="amount-{l.Alice}" value="20.00" """, html);
        Assert.Contains($"""name="amount-{l.Bob}" value="30.00" """, html);
        Assert.Contains($"""name="amount-{l.Carol}" value="" """, html);
        Assert.Contains($"""<input type="checkbox" name="participants" value="{l.Alice}" checked />""", html);
        Assert.DoesNotContain($"""<input type="checkbox" name="participants" value="{l.Carol}" checked />""", html);
        Assert.Contains($"""<option value="{l.Carol}" selected>Carol</option>""", html);
    }

    [Fact]
    public async Task An_equal_expense_becomes_a_shares_expense()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);

        var response = await Post(browser, l, await FormPage(browser, l), [.. Fields(l, amount: "10.00") .Where(f => f.Name != "mode"),
            ("mode", "shares"), Shares(l.Alice, "2"), Shares(l.Bob, "1"), Shares(l.Carol, "1")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var edited = Assert.Single(await EditsOf(l.Group.Id));
        Assert.Equal(new SharesSplit([new MemberShares(l.Alice, 2), new MemberShares(l.Bob, 1), new MemberShares(l.Carol, 1)]), edited.Split);
        Assert.Equal([new Split(l.Alice, 500), new Split(l.Bob, 250), new Split(l.Carol, 250)], edited.Splits);
    }

    [Fact]
    public async Task An_equal_expense_becomes_an_exact_one_starting_from_what_each_person_owes()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var form = await FormPage(browser, l);

        var response = await Post(browser, l, form, [.. Fields(l).Where(f => f.Name != "mode"),
            ("mode", "exact"), Owes(l.Alice, "30.00"), Owes(l.Bob, "40.00"), Owes(l.Carol, "20.00")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var edited = Assert.Single(await EditsOf(l.Group.Id));
        Assert.Equal(new ExactSplit([new MemberAmount(l.Alice, 3000), new MemberAmount(l.Bob, 4000), new MemberAmount(l.Carol, 2000)]), edited.Split);
        Assert.Equal(9000, edited.AmountMinor);
    }

    [Fact]
    public async Task Editing_only_the_amount_of_an_exact_expense_is_rejected_and_shown_with_its_amounts()
    {
        var l = await SeedLisbon();
        var steak = await SeedSteak(l);
        var browser = app.BrowserFor(_alice);
        var form = await FormPageOf(browser, l, steak);

        var response = await PostTo(browser, l, steak, form, "mode".With("exact"), "description".With("Steak night"), "amount".With("60"),
            "payer".With(l.Carol.ToString()), "participants".With(l.Alice.ToString()), "participants".With(l.Bob.ToString()),
            Owes(l.Alice, "20.00"), Owes(l.Bob, "30.00"), "paidOn".With("2026-10-01"));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("exact amounts must add up to the total", html);
        Assert.Contains($"""name="amount-{l.Alice}" value="20.00" """, html);
        Assert.Contains("""name="amount" value="60" """, html);
        Assert.Empty(await EditsOf(l.Group.Id));
    }

    [Fact]
    public async Task An_exact_expense_edited_with_new_amounts_that_add_up_is_saved()
    {
        var l = await SeedLisbon();
        var steak = await SeedSteak(l);
        var browser = app.BrowserFor(_alice);

        var response = await PostTo(browser, l, steak, await FormPageOf(browser, l, steak), "mode".With("exact"), "description".With("Steak night"),
            "amount".With("60"), "payer".With(l.Carol.ToString()), "participants".With(l.Alice.ToString()), "participants".With(l.Bob.ToString()),
            Owes(l.Alice, "25"), Owes(l.Bob, "35"), "paidOn".With("2026-10-01"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal([new Split(l.Alice, 2500), new Split(l.Bob, 3500)], Assert.Single(await EditsOf(l.Group.Id)).Splits);
    }

    [Fact]
    public async Task A_shares_expense_becomes_an_equal_one()
    {
        var l = await SeedLisbon();
        var flat = await SeedFlat(l);
        var browser = app.BrowserFor(_alice);

        await PostTo(browser, l, flat, await FormPageOf(browser, l, flat), "mode".With("equal"), "description".With("Flat"), "amount".With("10"),
            "payer".With(l.Alice.ToString()), "participants".With(l.Alice.ToString()), "participants".With(l.Bob.ToString()),
            "paidOn".With("2026-10-01"));

        var edited = Assert.Single(await EditsOf(l.Group.Id));
        Assert.Equal(new EqualSplit([l.Alice, l.Bob]), edited.Split);
        Assert.Equal([new Split(l.Alice, 500), new Split(l.Bob, 500)], edited.Splits);
    }

    [Fact]
    public async Task A_shares_expense_posted_as_shown_changes_nothing()
    {
        var l = await SeedLisbon();
        var flat = await SeedFlat(l);
        var browser = app.BrowserFor(_alice);

        var response = await PostTo(browser, l, flat, await FormPageOf(browser, l, flat), "mode".With("shares"), "description".With("Flat"),
            "amount".With("10.00"), "payer".With(l.Alice.ToString()), "participants".With(l.Alice.ToString()), "participants".With(l.Bob.ToString()),
            "participants".With(l.Carol.ToString()), Shares(l.Alice, "2"), Shares(l.Bob, "1"), Shares(l.Carol, "1"), "paidOn".With("2026-10-01"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Empty(await EditsOf(l.Group.Id));
    }

    [Fact]
    public async Task Changing_only_a_weight_is_a_change()
    {
        var l = await SeedLisbon();
        var flat = await SeedFlat(l);
        var browser = app.BrowserFor(_alice);

        await PostTo(browser, l, flat, await FormPageOf(browser, l, flat), "mode".With("shares"), "description".With("Flat"),
            "amount".With("10.00"), "payer".With(l.Alice.ToString()), "participants".With(l.Alice.ToString()), "participants".With(l.Bob.ToString()),
            "participants".With(l.Carol.ToString()), Shares(l.Alice, "2"), Shares(l.Bob, "2"), Shares(l.Carol, "1"), "paidOn".With("2026-10-01"));

        Assert.Equal([new Split(l.Alice, 400), new Split(l.Bob, 400), new Split(l.Carol, 200)], Assert.Single(await EditsOf(l.Group.Id)).Splits);
    }

    [Theory]
    [InlineData("shares", "abc", "every share must be a whole number")]
    [InlineData("shares", "", "every share must be a whole number")]
    [InlineData("shares", "1.5", "every share must be a whole number")]
    [InlineData("shares", "0", "every share must be a positive whole number")]
    [InlineData("shares", "-2", "every share must be a positive whole number")]
    [InlineData("exact", "abc", "every exact amount must be a number")]
    [InlineData("exact", "", "every exact amount must be a number")]
    [InlineData("exact", "1.234", "every exact amount must be a number")]
    [InlineData("exact", "-5", "every exact amount must be a number")]
    [InlineData("exact", "5", "exact amounts must add up to the total")]
    public async Task A_split_field_deciding_refuses_is_shown_back_as_typed_and_nothing_is_saved(string mode, string typed, string reason)
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        var field = mode == "shares" ? Shares(l.Bob, typed) : Owes(l.Bob, typed);

        var response = await Post(browser, l, await FormPage(browser, l), [.. Fields(l).Where(f => f.Name != "mode"),
            ("mode", mode), Shares(l.Alice, "1"), Shares(l.Carol, "1"), Owes(l.Alice, "30"), Owes(l.Carol, "30"), field]);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"""<p class="error" role="alert">{reason}</p>""", html);
        Assert.Contains($"""<input type="radio" name="mode" value="{mode}" checked />""", html);
        Assert.Contains($"""name="{field.Name}" value="{typed}" """, html);
        Assert.Empty(await EditsOf(l.Group.Id));
    }

    // ── Who may ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task S16_a_non_member_posting_is_404_and_edits_nothing()
    {
        var l = await SeedLisbon();
        var mallory = app.BrowserFor(UserId.New());
        var token = await Forms.TokenFrom(mallory, "/groups/new");

        var response = await Forms.Post(mallory, Path(l), token, Fields(l, amount: "120"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await EditsOf(l.Group.Id));
    }

    [Fact]
    public async Task A_missing_group_a_malformed_ids_an_unknown_and_a_removed_expense_posted_are_404_and_edit_nothing()
    {
        var l = await SeedLisbon();
        var removed = await l.Group.Expense("Taxi", 3000, l.Alice, [l.Alice]);
        await l.Group.RemoveExpense(removed);
        var browser = app.BrowserFor(_alice);
        var token = Forms.TokenIn(await FormPage(browser, l))!;

        var responses = new[]
        {
            await Forms.Post(browser, Path(GroupId.New(), l.Dinner), token, Fields(l, amount: "120")),
            await Forms.Post(browser, $"/groups/nonsense/expenses/{l.Dinner}/edit", token, Fields(l, amount: "120")),
            await Forms.Post(browser, Path(l.Group.Id, ExpenseId.New()), token, Fields(l, amount: "120")),
            await Forms.Post(browser, $"/groups/{l.Group.Id}/expenses/nonsense/edit", token, Fields(l, amount: "120")),
            await Forms.Post(browser, Path(l.Group.Id, removed), token, Fields(l, amount: "120")),
        };

        foreach (var response in responses)
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await EditsOf(l.Group.Id));
    }

    [Fact]
    public async Task A_non_member_is_told_nothing_even_by_a_form_that_would_be_rejected()
    {
        var l = await SeedLisbon();
        var mallory = app.BrowserFor(UserId.New());
        var token = await Forms.TokenFrom(mallory, "/groups/new");

        var response = await Forms.Post(mallory, Path(l), token, Fields(l, description: "", amount: "0"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

        var response = await Post(browser, l, form, Fields(l, amount: "120"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await EditsOf(l.Group.Id));
    }

    [Fact]
    public async Task Without_a_valid_antiforgery_token_it_is_400_and_edits_nothing()
    {
        var l = await SeedLisbon();
        var browser = app.BrowserFor(_alice);
        await FormPage(browser, l);

        var response = await Forms.Post(browser, Path(l), "forged", Fields(l, amount: "120"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await EditsOf(l.Group.Id));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static async Task<string> FormPage(HttpClient browser, Lisbon l)
    {
        var response = await browser.GetAsync(Path(l));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>The form as it is shown for Dinner, with these values in place of what it offered.</summary>
    private static (string Name, string Value)[] Fields(
        Lisbon l, string description = "Dinner", string amount = "90.00", MemberId? payer = null,
        MemberId[]? participants = null, string paidOn = "2026-10-01") =>
    [
        ("mode", "equal"),
        ("description", description),
        ("amount", amount),
        ("payer", (payer ?? l.Alice).ToString()),
        .. (participants ?? [l.Alice, l.Bob, l.Carol]).Select(p => ("participants", p.ToString())),
        ("paidOn", paidOn),
    ];

    private static (string Name, string Value)[] With((string Name, string Value)[] fields, string name, string value) =>
        [.. fields.Select(f => f.Name == name ? (f.Name, value) : f)];

    /// <summary>Posts <paramref name="fields"/> with the token <paramref name="form"/> carries.</summary>
    private static Task<HttpResponseMessage> Post(HttpClient browser, Lisbon l, string form, params (string Name, string Value)[] fields) =>
        Forms.Post(browser, Path(l), Forms.TokenIn(form)!, fields);

    private async Task<IReadOnlyList<ExpenseEdited>> EditsOf(GroupId group)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(group.Value)).Select(e => e.Data).OfType<ExpenseEdited>().ToList();
    }
}

internal static class FieldExtensions
{
    /// <summary>A posted field, written <c>"amount".With("60")</c>.</summary>
    public static (string Name, string Value) With(this string name, string value) => (name, value);
}
