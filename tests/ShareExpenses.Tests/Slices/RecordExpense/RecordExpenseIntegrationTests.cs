using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using ShareExpenses.Infrastructure.Marten;
using ShareExpenses.Shared;
using ShareExpenses.Slices.RecordExpense;
using ShareExpenses.Tests.Infrastructure;

namespace ShareExpenses.Tests.Slices.RecordExpense;

/// <summary>Recording expenses through HTTP, against the real store.</summary>
[Collection(AppCollection.Name)]
public class RecordExpenseIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();
    private readonly UserId _mallory = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    // ── Specs against the real store: Marten's fold must agree with the test fold ──

    [Fact]
    public async Task S1_records_an_equal_split_with_201_and_a_location()
    {
        var (groupId, alice, bob, carol) = await Lisbon();

        var response = await Record(groupId, new
        {
            description = "Dinner", amountMinor = 9000, payerMemberId = alice,
            split = new { mode = "equal", participants = new[] { alice, bob, carol } },
            paidOn = "2026-10-01",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var expenseId = (await response.Content.ReadFromJsonAsync<RecordedBody>())!.ExpenseId;
        Assert.Equal($"/api/groups/{groupId}/expenses/{expenseId}", response.Headers.Location?.OriginalString);

        var recorded = Assert.IsType<ExpenseRecorded>((await StreamOf(groupId)).Last());
        Assert.Equal((expenseId, "Dinner", 9000L, alice, new DateOnly(2026, 10, 1), _alice),
            (recorded.ExpenseId, recorded.Description, recorded.AmountMinor, recorded.PayerMemberId, recorded.PaidOn, recorded.By));
        Assert.Equal(new EqualSplit([alice, bob, carol]), recorded.Split);
        Assert.Equal([new Split(alice, 3000), new Split(bob, 3000), new Split(carol, 3000)], recorded.Splits);
    }

    [Fact]
    public async Task S7_shares_and_exact_splits_round_trip_through_the_store()
    {
        var (groupId, alice, bob, _) = await Lisbon();

        await Record(groupId, new
        {
            description = "Flat", amountMinor = 100, payerMemberId = alice,
            split = new { mode = "shares", shares = new[] { new { memberId = bob, shares = 2 }, new { memberId = alice, shares = 1 } } },
            paidOn = "2026-10-01",
        });
        await Record(groupId, new
        {
            description = "Steak night", amountMinor = 5000, payerMemberId = alice,
            split = new { mode = "exact", amounts = new[] { new { memberId = alice, amountMinor = 2000L }, new { memberId = bob, amountMinor = 3000L } } },
            paidOn = "2026-10-01",
        });

        var expenses = (await StreamOf(groupId)).OfType<ExpenseRecorded>().ToList();
        Assert.Equal(new SharesSplit([new MemberShares(alice, 1), new MemberShares(bob, 2)]), expenses[0].Split);
        Assert.Equal([new Split(alice, 33), new Split(bob, 67)], expenses[0].Splits);
        Assert.Equal(new ExactSplit([new MemberAmount(alice, 2000), new MemberAmount(bob, 3000)]), expenses[1].Split);
        Assert.Equal([new Split(alice, 2000), new Split(bob, 3000)], expenses[1].Splits);
    }

    [Fact]
    public async Task The_mode_is_stored_as_a_fixed_discriminator()
    {
        var (groupId, alice, _, _) = await Lisbon();

        await Record(groupId, Dinner(alice));

        await using var session = Store.QuerySession();
        var modes = await session.QueryAsync<string>(
            $"select data->'Split'->>'mode' from {MartenSetup.Schema}.mt_events where stream_id = ? and type = 'expense_recorded'",
            groupId.Value);
        Assert.Equal("equal", Assert.Single(modes));
    }

    // ── Shape errors: a malformed split does not read ─────────────────────────────

    [Theory]
    [InlineData("""{"mode":"halves","participants":["{alice}"]}""")]
    [InlineData("""{"participants":["{alice}"]}""")]
    [InlineData("""{"mode":"exact","amounts":[{"memberId":"{alice}"}]}""")]
    [InlineData("""{"mode":"shares","shares":[{"memberId":"{alice}"}]}""")]
    public async Task A_malformed_split_is_400_and_records_nothing(string split)
    {
        var (groupId, alice, _, _) = await Lisbon();

        var response = await RecordJson(groupId, alice, split.Replace("{alice}", alice.ToString()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await StreamOf(groupId)).OfType<ExpenseRecorded>());
    }

    [Fact]
    public async Task The_mode_need_not_come_first()
    {
        var (groupId, alice, _, _) = await Lisbon();

        var response = await RecordJson(groupId, alice, $$"""{"participants":["{{alice}}"],"mode":"equal"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Stored_under_a_stable_name_with_the_date_as_a_plain_date()
    {
        var (groupId, alice, _, _) = await Lisbon();

        await Record(groupId, Dinner(alice));

        await using var session = Store.QuerySession();
        var rows = await session.QueryAsync<string>(
            $"select type || ' ' || (data->>'PaidOn') from {MartenSetup.Schema}.mt_events where stream_id = ? order by version desc limit 1",
            groupId.Value);
        Assert.Equal("expense_recorded 2026-10-01", Assert.Single(rows));
    }

    [Fact]
    public async Task The_future_is_judged_by_the_apps_clock()
    {
        var (groupId, alice, _, _) = await Lisbon();
        var tomorrow = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime).AddDays(1);

        Assert.Equal(HttpStatusCode.Created, (await Record(groupId, Dinner(alice, tomorrow))).StatusCode);
        var response = await Record(groupId, Dinner(alice, tomorrow.AddDays(1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("date cannot be in the future", await response.Content.ReadAsStringAsync());
    }

    // ── Concurrency ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_phones_at_once_the_second_save_conflicts_and_appends_nothing()
    {
        var (groupId, alice, _, _) = await Lisbon();
        app.BeforeNextSave.Arm(async () =>
            Assert.Equal(HttpStatusCode.Created, (await Record(groupId, Dinner(alice))).StatusCode));

        var response = await Record(groupId, Dinner(alice));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single((await StreamOf(groupId)).OfType<ExpenseRecorded>());
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_rejects_an_invalid_expense_with_400_and_the_reason()
    {
        var (groupId, alice, _, _) = await Lisbon();

        var response = await Record(groupId, new
        {
            description = "Steak night", amountMinor = 5000, payerMemberId = alice,
            split = new { mode = "exact", amounts = new[] { new { memberId = alice, amountMinor = 4999L } } },
            paidOn = "2026-10-01",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("exact amounts must add up to the total", await response.Content.ReadAsStringAsync());
        Assert.Empty((await StreamOf(groupId)).OfType<ExpenseRecorded>());
    }

    [Fact]
    public async Task Post_answers_a_non_member_exactly_as_for_a_missing_or_malformed_group()
    {
        var (groupId, alice, _, _) = await Lisbon();
        var client = app.ClientFor(_mallory);

        HttpResponseMessage[] responses =
        [
            await client.PostAsJsonAsync($"/api/groups/{groupId}/expenses", Dinner(alice)),
            await client.PostAsJsonAsync($"/api/groups/{GroupId.New()}/expenses", Dinner(alice)),
            await client.PostAsJsonAsync("/api/groups/not-a-guid/expenses", Dinner(alice)),
        ];

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Single((await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()))).Distinct());
    }

    [Fact]
    public async Task Post_requires_a_signed_in_user()
    {
        var response = await app.CreateClient().PostAsJsonAsync($"/api/groups/{GroupId.New()}/expenses", Dinner(MemberId.New()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Amounts_travel_as_plain_json_numbers()
    {
        var (groupId, alice, _, _) = await Lisbon();

        var response = await app.ClientFor(_alice).PostAsync($"/api/groups/{groupId}/expenses", JsonContent.Create(
            JsonDocument.Parse($$"""
                {"description":"Dinner","amountMinor":1000000000000,"payerMemberId":"{{alice}}",
                 "split":{"mode":"equal","participants":["{{alice}}"]},"paidOn":"2026-10-01"}
                """).RootElement));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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

    private static object Dinner(MemberId payer, DateOnly? paidOn = null) => new
    {
        description = "Dinner", amountMinor = 9000, payerMemberId = payer,
        split = new { mode = "equal", participants = new[] { payer } },
        paidOn = (paidOn ?? new DateOnly(2026, 10, 1)).ToString("yyyy-MM-dd"),
    };

    private Task<HttpResponseMessage> RecordJson(GroupId groupId, MemberId payer, string split) =>
        app.ClientFor(_alice).PostAsync($"/api/groups/{groupId}/expenses", JsonContent.Create(JsonDocument.Parse($$"""
            {"description":"Dinner","amountMinor":9000,"payerMemberId":"{{payer}}","split":{{split}},"paidOn":"2026-10-01"}
            """).RootElement));

    private Task<HttpResponseMessage> Record(GroupId groupId, object body) =>
        app.ClientFor(_alice).PostAsJsonAsync($"/api/groups/{groupId}/expenses", body);

    private async Task<IReadOnlyList<object>> StreamOf(GroupId groupId)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(groupId.Value)).Select(e => e.Data).ToList();
    }

    private sealed record CreatedBody(GroupId GroupId, MemberId MemberId);

    private sealed record AddedBody(MemberId MemberId);

    private sealed record RecordedBody(ExpenseId ExpenseId);
}
