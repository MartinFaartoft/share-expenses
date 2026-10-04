using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using ShareExpenses.Infrastructure.Marten;
using ShareExpenses.Shared;
using ShareExpenses.Tests.Infrastructure;

namespace ShareExpenses.Tests.Slices.ViewGroup;

/// <summary>The group page through HTTP, from the activity Marten projects inline.</summary>
[Collection(AppCollection.Name)]
public class ViewGroupIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();
    private readonly UserId _mallory = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    [Fact]
    public async Task A_new_group_has_no_history_and_you_are_settled_up()
    {
        var (groupId, alice, _, _) = await Lisbon();

        var group = await View(groupId);

        Assert.Equal((groupId, "Lisbon trip", "GBP", alice, 0L), (group.GroupId, group.GroupName, group.Currency, group.You, group.BalanceMinor));
        Assert.Empty(group.History);
    }

    [Fact]
    public async Task Read_your_writes_the_expense_appears_and_your_standing_moves_the_moment_it_is_saved()
    {
        var (groupId, alice, bob, carol) = await Lisbon();

        await Record(groupId, alice, 9000, [alice, bob, carol]);
        var group = await View(groupId);

        Assert.Equal(6000, group.BalanceMinor);
        var expense = Assert.Single(group.History);
        Assert.Equal("expense", expense.GetProperty("kind").GetString());
        Assert.Equal("Dinner", expense.GetProperty("description").GetString());
        Assert.Equal("Alice", expense.GetProperty("payerName").GetString());
        Assert.Equal("equal", expense.GetProperty("split").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task A_settlement_moves_your_standing_and_joins_the_history_marked_by_kind()
    {
        var (groupId, alice, bob, carol) = await Lisbon();
        await Record(groupId, alice, 9000, [alice, bob, carol]);

        var paid = await app.ClientFor(_alice).PostAsJsonAsync($"/api/groups/{groupId}/settlements",
            new { fromMemberId = bob, toMemberId = alice, amountMinor = 3000, paidOn = "2026-10-02" });
        Assert.Equal(HttpStatusCode.Created, paid.StatusCode);
        var group = await View(groupId);

        Assert.Equal(3000, group.BalanceMinor);
        Assert.Equal(["settlement", "expense"], group.History.Select(h => h.GetProperty("kind").GetString()));
        var settlement = group.History[0];
        Assert.Equal((bob.ToString(), "Bob", alice.ToString(), "Alice", 3000L),
            (settlement.GetProperty("fromMemberId").GetString(), settlement.GetProperty("fromName").GetString(),
                settlement.GetProperty("toMemberId").GetString(), settlement.GetProperty("toName").GetString(),
                settlement.GetProperty("amountMinor").GetInt64()));
    }

    [Fact]
    public async Task The_activity_is_stored_as_its_own_document()
    {
        var (groupId, _, _, _) = await Lisbon();

        await using var session = Store.QuerySession();
        var rows = await session.QueryAsync<int>(
            $"select count(*) from {MartenSetup.Schema}.mt_doc_group_activity where id = ?", groupId.Value);
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

    private async Task<GroupBody> View(GroupId groupId)
    {
        var response = await app.ClientFor(_alice).GetAsync($"/api/groups/{groupId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GroupBody>())!;
    }

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);

    private sealed record GroupBody(
        GroupId GroupId, string GroupName, string Currency, MemberId You, long BalanceMinor, IReadOnlyList<JsonElement> History);
}
