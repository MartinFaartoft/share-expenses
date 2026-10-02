using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using ShareExpenses.Infrastructure.Marten;
using ShareExpenses.Shared;
using ShareExpenses.Tests.Infrastructure;

namespace ShareExpenses.Tests.Slices.ViewBalances;

/// <summary>The group page through HTTP, from the ledger Marten projects inline.</summary>
[Collection(AppCollection.Name)]
public class ViewBalancesIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();
    private readonly UserId _mallory = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    [Fact]
    public async Task A_new_group_shows_its_creator_at_zero()
    {
        var (groupId, alice, _, _) = await Lisbon();

        var ledger = await View(groupId);

        Assert.Equal(("Lisbon trip", "GBP", alice), (ledger.GroupName, ledger.Currency, ledger.You));
        Assert.Equal([("Alice", "joined", 0L), ("Bob", "placeholder", 0L), ("Carol", "placeholder", 0L)], Members(ledger));
        Assert.Empty(ledger.Expenses);
    }

    [Fact]
    public async Task Read_your_writes_the_balance_moves_the_moment_the_expense_is_saved()
    {
        var (groupId, alice, bob, carol) = await Lisbon();

        await Record(groupId, alice, 9000, [alice, bob, carol]);
        var ledger = await View(groupId);

        Assert.Equal([("Alice", "joined", 6000L), ("Bob", "placeholder", -3000L), ("Carol", "placeholder", -3000L)], Members(ledger));
        var expense = Assert.Single(ledger.Expenses);
        Assert.Equal(("Dinner", 9000L, alice), (expense.Description, expense.AmountMinor, expense.PayerMemberId));
        Assert.Equal("equal", expense.Split.GetProperty("mode").GetString());
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
    public async Task The_ledger_is_stored_as_its_own_document()
    {
        var (groupId, _, _, _) = await Lisbon();

        await using var session = Store.QuerySession();
        var rows = await session.QueryAsync<int>(
            $"select count(*) from {MartenSetup.Schema}.mt_doc_group_ledger where id = ?", groupId.Value);
        Assert.Equal(1, Assert.Single(rows));
    }

    [Fact]
    public async Task A_non_member_gets_exactly_what_a_missing_or_malformed_group_gets()
    {
        var (groupId, _, _, _) = await Lisbon();
        var client = app.ClientFor(_mallory);

        HttpResponseMessage[] responses =
        [
            await client.GetAsync($"/api/groups/{groupId}"),
            await client.GetAsync($"/api/groups/{GroupId.New()}"),
            await client.GetAsync("/api/groups/not-a-guid"),
        ];

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Single((await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()))).Distinct());
    }

    [Fact]
    public async Task Requires_a_signed_in_user()
    {
        var response = await app.CreateClient().GetAsync($"/api/groups/{GroupId.New()}");

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

    private async Task<LedgerBody> View(GroupId groupId)
    {
        var response = await app.ClientFor(_alice).GetAsync($"/api/groups/{groupId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LedgerBody>())!;
    }

    private static (string, string, long)[] Members(LedgerBody ledger) =>
        [.. ledger.Members.Select(m => (m.Name, m.Status, m.BalanceMinor))];

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);

    private sealed record LedgerBody(
        string GroupName, string Currency, MemberId You, IReadOnlyList<MemberBody> Members, IReadOnlyList<ExpenseBody> Expenses);

    private sealed record MemberBody(MemberId MemberId, string Name, string Status, long BalanceMinor);

    private sealed record ExpenseBody(
        ExpenseId ExpenseId, string Description, long AmountMinor, MemberId PayerMemberId, DateOnly PaidOn, JsonElement Split);
}
