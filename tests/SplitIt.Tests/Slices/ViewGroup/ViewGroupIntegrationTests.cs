using System.Net;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SplitIt.Infrastructure.Marten;
using SplitIt.Shared;
using SplitIt.Tests.Infrastructure;

namespace SplitIt.Tests.Slices.ViewGroup;

/// <summary>
/// The group page (spec §3; slice-07-view-group.md), from the activity Marten projects
/// inline as the group's events are saved: where you stand, the history chat style —
/// newest at the bottom, what you paid on the left — and the actions.
/// </summary>
[Collection(AppCollection.Name)]
public class ViewGroupIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    [Fact]
    public async Task Shows_the_group_with_a_way_home_and_a_way_to_add_an_expense()
    {
        var (group, _) = await Lisbon();

        var html = await PageOf(group.Id);

        Assert.Contains("<title>Lisbon trip · SplitIt</title>", html);
        Assert.Contains("""<a class="up" href="/" aria-label="Home">‹</a>""", html);
        Assert.Contains($"""<a class="fab" href="/groups/{group.Id}/expenses/new" aria-label="Add expense">+</a>""", html);
        Assert.Contains($"""<a href="/groups/{group.Id}/members/new">Add member</a>""", html);
        Assert.Contains("You're settled up", html);
        Assert.Contains("Nothing yet.", html);
    }

    [Fact]
    public async Task Read_your_writes_when_you_are_owed_it_says_so_and_settle_up_is_still_there()
    {
        var (group, bob) = await Lisbon();
        await group.Expense("Dinner", 9000, group.You, [group.You, bob]);

        var html = await PageOf(group.Id);

        Assert.Contains("You are owed <strong>£45.00</strong>", html);
        Assert.Contains($"""<a class="button" href="/groups/{group.Id}/settle-up">Settle up</a>""", html);
    }

    [Fact]
    public async Task When_you_owe_it_says_so_beside_settle_up()
    {
        var (group, bob) = await Lisbon();
        await group.Expense("Taxi", 14000, bob, [group.You, bob]);

        var html = await PageOf(group.Id);

        Assert.Contains("You owe <strong>£70.00</strong>", html);
        Assert.Contains($"""<a class="button" href="/groups/{group.Id}/settle-up">Settle up</a>""", html);
    }

    [Fact]
    public async Task History_is_chat_style_what_you_paid_on_the_left_others_on_the_right()
    {
        var (group, bob) = await Lisbon();
        await group.Expense("Dinner", 9000, group.You, [group.You, bob], new DateOnly(2026, 10, 1));
        await group.Expense("Taxi", 3000, bob, [group.You, bob], new DateOnly(2026, 10, 2));
        await group.Settlement(bob, group.You, 1500, new DateOnly(2026, 10, 3));

        var html = await PageOf(group.Id);

        Assert.Matches("""<li class="mine expense">\s*<a class="card"[^>]*>\s*<span class="what">Dinner</span>""", html);
        Assert.Matches("""<li class="theirs expense">\s*<a class="card"[^>]*>\s*<span class="what">Taxi</span>""", html);
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
        var (group, _) = await Lisbon();

        Assert.Contains($"""<a class="balance" href="/groups/{group.Id}/balances">""", await PageOf(group.Id));
    }

    [Fact]
    public async Task The_activity_is_stored_as_its_own_document()
    {
        var (group, _) = await Lisbon();

        await using var session = app.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var rows = await session.QueryAsync<int>(
            $"select count(*) from {MartenSetup.Schema}.mt_doc_group_activity where id = ?", group.Id.Value);
        Assert.Equal(1, Assert.Single(rows));
    }

    [Fact]
    public async Task A_non_member_gets_the_same_404_page_as_for_a_missing_or_malformed_group()
    {
        var (group, _) = await Lisbon();
        var mallory = app.ClientFor(UserId.New());

        HttpResponseMessage[] responses =
        [
            await mallory.GetAsync($"/groups/{group.Id}"),
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

    /// <summary>Alice's "Lisbon trip" in GBP, with Bob as a placeholder.</summary>
    private async Task<(SeededGroup Group, MemberId Bob)> Lisbon()
    {
        var group = await new Seed(app).Group(_alice);
        return (group, await group.Member("Bob"));
    }

    private async Task<string> PageOf(GroupId groupId)
    {
        var response = await app.ClientFor(_alice).GetAsync($"/groups/{groupId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // As the user reads it: Razor encodes £, ' and the like in the markup.
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }
}
