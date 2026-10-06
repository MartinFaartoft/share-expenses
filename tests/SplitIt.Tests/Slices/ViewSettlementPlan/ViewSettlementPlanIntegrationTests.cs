using System.Net;
using SplitIt.Shared;
using SplitIt.Tests.Infrastructure;

namespace SplitIt.Tests.Slices.ViewSettlementPlan;

/// <summary>The Settle up screen, folded live from the real store per request.</summary>
[Collection(AppCollection.Name)]
public class ViewSettlementPlanIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    private async Task<(SeededGroup Group, MemberId Bob, MemberId Carol)> Lisbon()
    {
        var group = await new Seed(app).Group(_alice);
        return (group, await group.Member("Bob"), await group.Member("Carol"));
    }

    private async Task<string> PageOf(GroupId group, string query = "")
    {
        var response = await app.BrowserFor(_alice).GetAsync($"/groups/{group}/settle-up{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_group_with_nothing_owed_says_so_and_still_offers_any_payment()
    {
        var (group, _, _) = await Lisbon();

        var html = await PageOf(group.Id);

        Assert.Contains("<title>Settle up · SplitIt</title>", html);
        Assert.Contains($"""<a class="up" href="/groups/{group.Id}" aria-label="Back">‹</a>""", html);
        Assert.Contains("You're all settled up.", html);
        Assert.Contains($"""action="/groups/{group.Id}/settlements" """.TrimEnd(), html);
        Assert.NotNull(Forms.TokenIn(html));
    }

    [Fact]
    public async Task The_plan_lists_who_pays_whom_with_a_Paid_button_for_each_line()
    {
        var (group, bob, carol) = await Lisbon();
        await group.Expense("Dinner", 9000, group.You, [group.You, bob, carol]);

        var html = await PageOf(group.Id);

        Assert.Contains("<strong>Bob</strong> pays", html);
        Assert.Contains("<strong>Carol</strong> pays", html);
        Assert.Contains("£30.00", html);
        Assert.Contains($"""<input type="hidden" name="from" value="{bob}" />""", html);
        Assert.Contains($"""<input type="hidden" name="to" value="{group.You}" />""", html);
        Assert.Contains("""<input type="hidden" name="amount" value="30.00" />""", html);
        Assert.Equal(2, html.Split(">Paid</button>").Length - 1);
    }

    [Fact]
    public async Task A_recorded_settlement_leaves_the_plan_the_moment_it_is_saved()
    {
        var (group, bob, carol) = await Lisbon();
        await group.Expense("Dinner", 9000, group.You, [group.You, bob, carol]);

        await group.Settlement(bob, group.You, 3000);

        var html = await PageOf(group.Id);
        Assert.DoesNotContain("<strong>Bob</strong> pays", html);
        Assert.Contains("<strong>Carol</strong> pays", html);
    }

    [Fact]
    public async Task Your_own_lines_read_as_you()
    {
        var (group, bob, carol) = await Lisbon();
        await group.Expense("Dinner", 9000, group.You, [group.You, bob, carol]);
        await group.Expense("Cab", 3000, bob, [group.You, bob]);

        var html = await PageOf(group.Id);

        Assert.Matches(@"<strong>Carol</strong> pays\s+<strong>You</strong>", html);
    }

    [Fact]
    public async Task A_rejection_from_the_form_is_shown_as_escaped_text()
    {
        var (group, _, _) = await Lisbon();

        var response = await app.BrowserFor(_alice).GetAsync($"/groups/{group.Id}/settle-up?error=%3Cb%3Enope%3C%2Fb%3E");

        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<b>nope</b>", html);
        Assert.Matches("""<p class="error" role="alert">&#x3C;b&#x3E;nope|<p class="error" role="alert">&lt;b&gt;nope""", html);
    }

    [Fact]
    public async Task A_non_member_a_missing_group_and_a_malformed_id_are_the_same_404()
    {
        var (group, _, _) = await Lisbon();
        var mallory = app.BrowserFor(UserId.New());

        var stranger = await mallory.GetAsync($"/groups/{group.Id}/settle-up");
        var missing = await mallory.GetAsync($"/groups/{GroupId.New()}/settle-up");
        var malformed = await mallory.GetAsync("/groups/nonsense/settle-up");

        foreach (var response in new[] { stranger, missing, malformed })
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await stranger.Content.ReadAsStringAsync();
        Assert.Equal(body, await missing.Content.ReadAsStringAsync());
        Assert.Equal(body, await malformed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signed_out_it_redirects_to_sign_in_and_back()
    {
        var (group, _, _) = await Lisbon();

        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync($"/groups/{group.Id}/settle-up");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/sign-in?returnUrl=%2Fgroups%2F{group.Id}%2Fsettle-up", response.Headers.Location?.OriginalString);
    }
}
