using System.Net;
using System.Text.RegularExpressions;
using SplitIt.Shared;
using SplitIt.Tests.Infrastructure;

namespace SplitIt.Tests.Slices.ViewBalances;

/// <summary>The Balances screen, folded live from the real store per request.</summary>
[Collection(AppCollection.Name)]
public partial class ViewBalancesIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    [Fact]
    public async Task A_new_group_shows_everyone_settled_up_with_a_way_back_and_to_settle_up()
    {
        var (group, _, _) = await Lisbon();

        var html = await PageOf(group.Id);

        Assert.Contains("<title>Balances · SplitIt</title>", html);
        Assert.Contains($"""<a href="/groups/{group.Id}">← Lisbon trip</a>""", html);
        Assert.Equal(
            [("Alice (you)", "settled up"), ("Bob — placeholder", "settled up"), ("Carol — placeholder", "settled up")],
            Lines(html));
        Assert.Contains($"""<a class="button" href="/groups/{group.Id}/settle-up">Settle up</a>""", html);
    }

    [Fact]
    public async Task Read_your_writes_balances_move_the_moment_the_expense_is_saved()
    {
        var (group, bob, carol) = await Lisbon();

        await group.Expense("Dinner", 9000, group.You, [group.You, bob, carol]);

        Assert.Equal(
            [("Alice (you)", "is owed £60.00"), ("Bob — placeholder", "owes £30.00"), ("Carol — placeholder", "owes £30.00")],
            Lines(await PageOf(group.Id)));
    }

    [Fact]
    public async Task A_settlement_moves_balances()
    {
        var (group, bob, carol) = await Lisbon();
        await group.Expense("Dinner", 9000, group.You, [group.You, bob, carol]);

        await group.Settlement(bob, group.You, 3000);

        Assert.Equal(
            [("Alice (you)", "is owed £30.00"), ("Bob — placeholder", "settled up"), ("Carol — placeholder", "owes £30.00")],
            Lines(await PageOf(group.Id)));
    }

    [Fact]
    public async Task An_invited_slot_shows_as_invited_until_its_deadline()
    {
        var (group, bob, _) = await Lisbon();
        await group.Invite(bob, $"bob-{Guid.NewGuid():N}@example.com");
        try
        {
            Assert.Equal("Bob — invited", Lines(await PageOf(group.Id))[1].Who);

            app.Clock.Offset = TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1);
            Assert.Equal("Bob — placeholder", Lines(await PageOf(group.Id))[1].Who);
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task A_joined_member_shows_no_status()
    {
        var (group, _, _) = await Lisbon();
        await group.Joined("Dave", UserId.New());

        Assert.Equal("Dave", Lines(await PageOf(group.Id))[3].Who);
    }

    [Fact]
    public async Task A_non_member_gets_the_same_404_page_as_for_a_missing_or_malformed_group()
    {
        var (group, _, _) = await Lisbon();
        var mallory = app.ClientFor(UserId.New());

        HttpResponseMessage[] responses =
        [
            await mallory.GetAsync($"/groups/{group.Id}/balances"),
            await mallory.GetAsync($"/groups/{GroupId.New()}/balances"),
            await mallory.GetAsync("/groups/not-a-guid/balances"),
        ];

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Single((await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()))).Distinct());
    }

    [Fact]
    public async Task Signed_out_it_redirects_to_sign_in_and_back()
    {
        var groupId = GroupId.New();

        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync($"/groups/{groupId}/balances");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/sign-in?returnUrl=%2Fgroups%2F{groupId}%2Fbalances", response.Headers.Location?.OriginalString);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Alice's "Lisbon trip" in GBP, with Bob and Carol as placeholders.</summary>
    private async Task<(SeededGroup Group, MemberId Bob, MemberId Carol)> Lisbon()
    {
        var group = await new Seed(app).Group(_alice);
        return (group, await group.Member("Bob"), await group.Member("Carol"));
    }

    private async Task<string> PageOf(GroupId groupId)
    {
        var response = await app.ClientFor(_alice).GetAsync($"/groups/{groupId}/balances");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // As the user reads it: Razor encodes £, — and the like in the markup.
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Each member line, in order: who, and where they stand.</summary>
    private static (string Who, string Standing)[] Lines(string html) =>
        [.. MemberLine().Matches(html).Select(m => (m.Groups[1].Value, m.Groups[2].Value))];

    [GeneratedRegex("""<li>\s*<span>([^<]*)</span>\s*<span class="amount">([^<]*)</span>""")]
    private static partial Regex MemberLine();
}
