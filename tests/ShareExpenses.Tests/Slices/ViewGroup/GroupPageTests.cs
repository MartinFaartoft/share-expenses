using System.Net;
using System.Net.Http.Json;
using ShareExpenses.Shared;
using ShareExpenses.Tests.Infrastructure;

namespace ShareExpenses.Tests.Slices.ViewGroup;

/// <summary>
/// The group page (spec §3; slice-07-view-group.md): HTML over the View group read
/// model. Where you stand, the history chat style — newest at the bottom, what you paid
/// on the left — and the actions.
/// </summary>
[Collection(AppCollection.Name)]
public class GroupPageTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    [Fact]
    public async Task Shows_the_group_with_a_way_home_and_a_way_to_add_an_expense()
    {
        var (groupId, _, _) = await Lisbon();

        var html = await PageOf(groupId);

        Assert.Contains("<title>Lisbon trip · Shared expenses</title>", html);
        Assert.Contains("""<a href="/">← Home</a>""", html);
        Assert.Contains($"""<a class="button" href="/groups/{groupId}/expenses/new">Add expense</a>""", html);
        Assert.Contains("You're settled up", html);
        Assert.Contains("Nothing yet.", html);
    }

    [Fact]
    public async Task When_you_are_owed_it_says_so_and_settle_up_is_still_there()
    {
        var (groupId, alice, bob) = await Lisbon();
        await Record(groupId, "Dinner", 9000, alice, [alice, bob]);

        var html = await PageOf(groupId);

        Assert.Contains("You are owed <strong>£45.00</strong>", html);
        Assert.Contains($"""<a class="button" href="/groups/{groupId}/settle-up">Settle up</a>""", html);
    }

    [Fact]
    public async Task When_you_owe_it_says_so_beside_settle_up()
    {
        var (groupId, alice, bob) = await Lisbon();
        await Record(groupId, "Taxi", 14000, bob, [alice, bob]);

        var html = await PageOf(groupId);

        Assert.Contains("You owe <strong>£70.00</strong>", html);
        Assert.Contains($"""<a class="button" href="/groups/{groupId}/settle-up">Settle up</a>""", html);
    }

    [Fact]
    public async Task History_is_chat_style_what_you_paid_on_the_left_others_on_the_right()
    {
        var (groupId, alice, bob) = await Lisbon();
        await Record(groupId, "Dinner", 9000, alice, [alice, bob], "2026-10-01");
        await Record(groupId, "Taxi", 3000, bob, [alice, bob], "2026-10-02");
        await Settle(groupId, bob, alice, 1500, "2026-10-03");

        var html = await PageOf(groupId);

        Assert.Contains("""<li class="mine expense"><span class="what">Dinner</span>""", html);
        Assert.Contains("""<li class="theirs expense"><span class="what">Taxi</span>""", html);
        Assert.Contains("You paid · 1 Oct 2026", html);
        Assert.Contains("Bob paid · 2 Oct 2026", html);
        Assert.Contains("""<li class="theirs settlement"><span class="what">Bob paid you</span>""", html);

        // Newest first in the markup; the stylesheet's column-reverse puts it at the bottom.
        Assert.True(html.IndexOf("Bob paid you", StringComparison.Ordinal) < html.IndexOf("Taxi", StringComparison.Ordinal));
        Assert.True(html.IndexOf("Taxi", StringComparison.Ordinal) < html.IndexOf("Dinner", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Links_to_the_balances()
    {
        var (groupId, _, _) = await Lisbon();

        Assert.Contains($"""<a href="/groups/{groupId}/balances">Balances</a>""", await PageOf(groupId));
    }

    [Fact]
    public async Task A_non_member_gets_the_same_404_page_as_for_a_missing_or_malformed_group()
    {
        var (groupId, _, _) = await Lisbon();
        var mallory = app.ClientFor(UserId.New());

        HttpResponseMessage[] responses =
        [
            await mallory.GetAsync($"/groups/{groupId}"),
            await mallory.GetAsync($"/groups/{GroupId.New()}"),
            await mallory.GetAsync("/groups/not-a-guid"),
        ];

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        Assert.Contains("That group doesn't exist, or isn't one of yours.", WebUtility.HtmlDecode(bodies[0]));
    }

    [Fact]
    public async Task Signed_out_it_redirects_to_sign_in_and_back()
    {
        var groupId = GroupId.New();

        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync($"/groups/{groupId}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/sign-in?returnUrl=%2Fgroups%2F{groupId}", response.Headers.Location?.OriginalString);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Alice creates "Lisbon trip" in GBP and adds Bob as a placeholder.</summary>
    private async Task<(GroupId Group, MemberId Alice, MemberId Bob)> Lisbon()
    {
        var client = app.ClientFor(_alice);
        var created = await (await client.PostAsJsonAsync("/api/groups",
            new { groupName = "Lisbon trip", currency = "GBP", memberName = "Alice" })).Content.ReadFromJsonAsync<CreatedBody>();
        var bob = (await (await client.PostAsJsonAsync($"/api/groups/{created!.GroupId}/members", new { displayName = "Bob" }))
            .Content.ReadFromJsonAsync<AddedBody>())!.MemberId;
        return (created.GroupId, created.MemberId, bob);
    }

    private async Task Record(GroupId groupId, string description, long amount, MemberId payer, MemberId[] sharers,
        string paidOn = "2026-10-01") =>
        Assert.Equal(HttpStatusCode.Created, (await app.ClientFor(_alice).PostAsJsonAsync($"/api/groups/{groupId}/expenses", new
        {
            description, amountMinor = amount, payerMemberId = payer,
            split = new { mode = "equal", participants = sharers }, paidOn,
        })).StatusCode);

    private async Task Settle(GroupId groupId, MemberId from, MemberId to, long amount, string paidOn) =>
        Assert.Equal(HttpStatusCode.Created, (await app.ClientFor(_alice).PostAsJsonAsync($"/api/groups/{groupId}/settlements",
            new { fromMemberId = from, toMemberId = to, amountMinor = amount, paidOn })).StatusCode);

    private async Task<string> PageOf(GroupId groupId)
    {
        var response = await app.ClientFor(_alice).GetAsync($"/groups/{groupId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // As the user reads it: Razor encodes £, ' and the like in the markup.
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);
}
