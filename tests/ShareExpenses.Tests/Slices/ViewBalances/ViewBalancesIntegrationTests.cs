using System.Net;
using System.Net.Http.Json;
using ShareExpenses.Shared;
using ShareExpenses.Tests.Infrastructure;

namespace ShareExpenses.Tests.Slices.ViewBalances;

/// <summary>Everyone's balances through HTTP: the JSON read model and the screen.</summary>
[Collection(AppCollection.Name)]
public class ViewBalancesIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    [Fact]
    public async Task A_new_group_shows_everyone_at_zero()
    {
        var (groupId, alice, _, _) = await Lisbon();

        var balances = await View(groupId);

        Assert.Equal((groupId, "Lisbon trip", "GBP", alice), (balances.GroupId, balances.GroupName, balances.Currency, balances.You));
        Assert.Equal([("Alice", "joined", 0L), ("Bob", "placeholder", 0L), ("Carol", "placeholder", 0L)], Members(balances));
    }

    [Fact]
    public async Task Read_your_writes_balances_move_the_moment_the_expense_is_saved()
    {
        var (groupId, alice, bob, carol) = await Lisbon();

        await Record(groupId, alice, 9000, [alice, bob, carol]);

        Assert.Equal(
            [("Alice", "joined", 6000L), ("Bob", "placeholder", -3000L), ("Carol", "placeholder", -3000L)],
            Members(await View(groupId)));
    }

    [Fact]
    public async Task A_settlement_moves_balances()
    {
        var (groupId, alice, bob, carol) = await Lisbon();
        await Record(groupId, alice, 9000, [alice, bob, carol]);

        var paid = await app.ClientFor(_alice).PostAsJsonAsync($"/api/groups/{groupId}/settlements",
            new { fromMemberId = bob, toMemberId = alice, amountMinor = 3000, paidOn = "2026-10-02" });
        Assert.Equal(HttpStatusCode.Created, paid.StatusCode);

        Assert.Equal(
            [("Alice", "joined", 3000L), ("Bob", "placeholder", 0L), ("Carol", "placeholder", -3000L)],
            Members(await View(groupId)));
    }

    [Fact]
    public async Task An_invited_slot_shows_as_invited_until_its_deadline()
    {
        var (groupId, _, bob, _) = await Lisbon();
        var invited = await app.ClientFor(_alice).PostAsJsonAsync(
            $"/api/groups/{groupId}/members/{bob}/invite", new { email = $"bob-{Guid.NewGuid():N}@example.com" });
        Assert.Equal(HttpStatusCode.NoContent, invited.StatusCode);
        try
        {
            Assert.Equal("invited", (await View(groupId)).Members[1].Status);

            app.Clock.Offset = TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1);
            Assert.Equal("placeholder", (await View(groupId)).Members[1].Status);
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task The_page_shows_everyones_standing_with_a_way_back_and_to_settle_up()
    {
        var (groupId, alice, bob, _) = await Lisbon();
        await Record(groupId, alice, 9000, [alice, bob]);

        var response = await app.ClientFor(_alice).GetAsync($"/groups/{groupId}/balances");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains($"""<a href="/groups/{groupId}">← Lisbon trip</a>""", html);
        Assert.Contains("<span>Alice (you)</span>", html);
        Assert.Contains("is owed £45.00", html);
        Assert.Contains("<span>Bob — placeholder</span>", html);
        Assert.Contains("owes £45.00", html);
        Assert.Contains("<span>Carol — placeholder</span>", html);
        Assert.Contains("settled up", html);
        Assert.Contains($"""<a class="button" href="/groups/{groupId}/settle-up">Settle up</a>""", html);
    }

    [Fact]
    public async Task A_non_member_gets_exactly_what_a_missing_or_malformed_group_gets()
    {
        var (groupId, _, _, _) = await Lisbon();
        var client = app.ClientFor(UserId.New());

        foreach (var prefix in new[] { "/api/groups", "/groups" })
        {
            HttpResponseMessage[] responses =
            [
                await client.GetAsync($"{prefix}/{groupId}/balances"),
                await client.GetAsync($"{prefix}/{GroupId.New()}/balances"),
                await client.GetAsync($"{prefix}/not-a-guid/balances"),
            ];

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
            Assert.Single((await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()))).Distinct());
        }
    }

    [Fact]
    public async Task Requires_a_signed_in_user()
    {
        var response = await app.CreateClient().GetAsync($"/api/groups/{GroupId.New()}/balances");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Alice creates "Lisbon trip" and adds Bob and Carol as placeholders.</summary>
    private async Task<(GroupId Group, MemberId Alice, MemberId Bob, MemberId Carol)> Lisbon()
    {
        var client = app.ClientFor(_alice);
        var created = await (await client.PostAsJsonAsync("/api/groups",
            new { groupName = "Lisbon trip", currency = "GBP", memberName = "Alice" })).Content.ReadFromJsonAsync<CreatedBody>();
        async Task<MemberId> Add(string name) =>
            (await (await client.PostAsJsonAsync($"/api/groups/{created!.GroupId}/members", new { displayName = name }))
                .Content.ReadFromJsonAsync<AddedBody>())!.MemberId;
        return (created!.GroupId, created.MemberId, await Add("Bob"), await Add("Carol"));
    }

    private async Task Record(GroupId groupId, MemberId payer, long amount, MemberId[] sharers)
    {
        var response = await app.ClientFor(_alice).PostAsJsonAsync($"/api/groups/{groupId}/expenses", new
        {
            description = "Dinner", amountMinor = amount, payerMemberId = payer,
            split = new { mode = "equal", participants = sharers },
            paidOn = "2026-10-01",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<BalancesBody> View(GroupId groupId)
    {
        var response = await app.ClientFor(_alice).GetAsync($"/api/groups/{groupId}/balances");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BalancesBody>())!;
    }

    private static (string, string, long)[] Members(BalancesBody balances) =>
        [.. balances.Members.Select(m => (m.Name, m.Status, m.BalanceMinor))];

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);

    private sealed record BalancesBody(
        GroupId GroupId, string GroupName, string Currency, MemberId You, IReadOnlyList<MemberBody> Members);

    private sealed record MemberBody(MemberId MemberId, string Name, string Status, long BalanceMinor);
}
