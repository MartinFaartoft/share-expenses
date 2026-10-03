using System.Net;
using System.Net.Http.Json;
using ShareExpenses.Shared;
using ShareExpenses.Tests.Infrastructure;

namespace ShareExpenses.Tests.Slices.ViewSettlementPlan;

/// <summary>The settle-up plan through HTTP, folded live from the real store.</summary>
[Collection(AppCollection.Name)]
public class ViewSettlementPlanIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();
    private readonly UserId _mallory = UserId.New();

    [Fact]
    public async Task The_plan_follows_expenses_and_settlements_as_they_are_recorded()
    {
        var (groupId, alice, bob, carol) = await Lisbon();
        Assert.Empty((await Plan(groupId)).Transfers);

        await Post(groupId, "expenses", new
        {
            description = "Dinner", amountMinor = 9000, payerMemberId = alice,
            split = new { mode = "equal", participants = new[] { alice, bob, carol } }, paidOn = "2026-10-01",
        });
        Assert.Equal(
            [new TransferBody(bob, "Bob", alice, "Alice", 3000), new TransferBody(carol, "Carol", alice, "Alice", 3000)],
            (await Plan(groupId)).Transfers);

        // "Paid" on Bob's line sends exactly its from, to and amount.
        await Post(groupId, "settlements", new { fromMemberId = bob, toMemberId = alice, amountMinor = 3000, paidOn = "2026-10-02" });
        var plan = await Plan(groupId);

        Assert.Equal(("GBP", alice), (plan.Currency, plan.You));
        Assert.Equal([new TransferBody(carol, "Carol", alice, "Alice", 3000)], plan.Transfers);
    }

    [Fact]
    public async Task A_non_member_gets_exactly_what_a_missing_or_malformed_group_gets()
    {
        var (groupId, _, _, _) = await Lisbon();
        var client = app.ClientFor(_mallory);

        HttpResponseMessage[] responses =
        [
            await client.GetAsync($"/api/groups/{groupId}/settlement-plan"),
            await client.GetAsync($"/api/groups/{GroupId.New()}/settlement-plan"),
            await client.GetAsync("/api/groups/not-a-guid/settlement-plan"),
        ];

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Single((await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()))).Distinct());
    }

    [Fact]
    public async Task Requires_a_signed_in_user()
    {
        var response = await app.CreateClient().GetAsync($"/api/groups/{GroupId.New()}/settlement-plan");

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

    private async Task Post(GroupId groupId, string what, object body) =>
        Assert.Equal(HttpStatusCode.Created,
            (await app.ClientFor(_alice).PostAsJsonAsync($"/api/groups/{groupId}/{what}", body)).StatusCode);

    private async Task<PlanBody> Plan(GroupId groupId)
    {
        var response = await app.ClientFor(_alice).GetAsync($"/api/groups/{groupId}/settlement-plan");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PlanBody>())!;
    }

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);

    private sealed record PlanBody(string Currency, MemberId You, IReadOnlyList<TransferBody> Transfers);

    private sealed record TransferBody(MemberId FromMemberId, string FromName, MemberId ToMemberId, string ToName, long AmountMinor);
}
