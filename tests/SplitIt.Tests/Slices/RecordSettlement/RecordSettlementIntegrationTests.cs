using System.Net;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SplitIt.Shared;
using SplitIt.Slices.RecordSettlement;
using SplitIt.Tests.Infrastructure;

namespace SplitIt.Tests.Slices.RecordSettlement;

/// <summary>
/// The Settle up screen's forms (slice-09-record-settlement.md), as a browser submits
/// them: take the page's token, post it with the settlement id the form carries.
/// </summary>
[Collection(AppCollection.Name)]
public class RecordSettlementIntegrationTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    private string Today => app.Clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd");

    private async Task<(SeededGroup Group, MemberId Bob)> Lisbon()
    {
        var group = await new Seed(app).Group(_alice);
        return (group, await group.Member("Bob"));
    }

    private Task<HttpResponseMessage> Submit(
        HttpClient browser, string token, GroupId group, MemberId? from, MemberId? to, string amount,
        SettlementId? id = null, string? paidOn = null) =>
        Forms.Post(browser, $"/groups/{group}/settlements", token,
            ("settlementId", (id ?? SettlementId.New()).ToString()),
            ("from", from?.ToString() ?? "nonsense"),
            ("to", to?.ToString() ?? "nonsense"),
            ("amount", amount),
            ("paidOn", paidOn ?? Today));

    private async Task<IReadOnlyList<SettlementRecorded>> SettlementsOf(GroupId group)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(group.Value)).Select(e => e.Data).OfType<SettlementRecorded>().ToList();
    }

    private async Task<(HttpClient Browser, string Token)> Page(GroupId group, UserId? user = null)
    {
        var browser = app.BrowserFor(user ?? _alice);
        return (browser, await Forms.TokenFrom(browser, $"/groups/{group}/settle-up"));
    }

    [Fact]
    public async Task Records_a_payment_and_goes_back_to_settle_up()
    {
        var (group, bob) = await Lisbon();
        var (browser, token) = await Page(group.Id);
        var id = SettlementId.New();

        var response = await Submit(browser, token, group.Id, bob, group.You, "30.00", id);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{group.Id}/settle-up", response.Headers.Location?.OriginalString);
        var recorded = Assert.Single(await SettlementsOf(group.Id));
        Assert.Equal((id, bob, group.You, 3000L, _alice), (recorded.SettlementId, recorded.FromMemberId, recorded.ToMemberId, recorded.AmountMinor, recorded.By));
        Assert.Equal(Today, recorded.PaidOn.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task A_rejection_goes_back_with_why_and_records_nothing()
    {
        var (group, _) = await Lisbon();
        var (browser, token) = await Page(group.Id);

        var response = await Submit(browser, token, group.Id, group.You, group.You, "30.00");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/groups/{group.Id}/settle-up?error=a%20member%20cannot%20pay%20themselves", response.Headers.Location?.OriginalString);
        Assert.Empty(await SettlementsOf(group.Id));
    }

    [Fact]
    public async Task An_amount_that_is_not_a_number_is_not_positive()
    {
        var (group, bob) = await Lisbon();
        var (browser, token) = await Page(group.Id);

        var response = await Submit(browser, token, group.Id, bob, group.You, "lots");

        Assert.Contains("amount%20must%20be%20positive", response.Headers.Location?.OriginalString);
        Assert.Empty(await SettlementsOf(group.Id));
    }

    [Fact]
    public async Task The_same_form_submitted_twice_records_one_payment()
    {
        var (group, bob) = await Lisbon();
        var (browser, token) = await Page(group.Id);
        var id = SettlementId.New();

        await Submit(browser, token, group.Id, bob, group.You, "30.00", id);
        var second = await Submit(browser, token, group.Id, bob, group.You, "30.00", id);

        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal($"/groups/{group.Id}/settle-up", second.Headers.Location?.OriginalString);
        Assert.Single(await SettlementsOf(group.Id));
    }

    [Fact]
    public async Task Two_saves_to_the_group_at_the_same_instant_the_loser_gets_409()
    {
        var (group, bob) = await Lisbon();
        var (browser, token) = await Page(group.Id);
        app.BeforeNextSave.Arm(async () =>
        {
            await using var session = Store.LightweightSession();
            session.Events.Append(group.Id.Value, new SplitIt.Slices.CreateGroup.MemberAdded(MemberId.New(), "Dave", _alice));
            await session.SaveChangesAsync();
        });

        var response = await Submit(browser, token, group.Id, bob, group.You, "30.00");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await SettlementsOf(group.Id));
    }

    [Fact]
    public async Task A_non_member_gets_the_404_and_records_nothing()
    {
        var (group, bob) = await Lisbon();
        var mallory = UserId.New();
        var other = await new Seed(app).Group(mallory);
        var (browser, token) = await Page(other.Id, mallory);

        var response = await Submit(browser, token, group.Id, bob, group.You, "30.00");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await SettlementsOf(group.Id));
    }

    [Fact]
    public async Task Without_a_valid_antiforgery_token_it_is_400_and_records_nothing()
    {
        var (group, bob) = await Lisbon();
        var (browser, _) = await Page(group.Id);

        var response = await Submit(browser, "forged", group.Id, bob, group.You, "30.00");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await SettlementsOf(group.Id));
    }

    [Fact]
    public async Task An_archived_group_records_nothing_and_settle_up_says_why()
    {
        var (group, bob) = await Lisbon();
        var (browser, token) = await Page(group.Id);
        await group.Archive();

        var response = await Submit(browser, token, group.Id, bob, group.You, "30.00");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=group%20is%20archived", response.Headers.Location?.OriginalString);
        Assert.Empty(await SettlementsOf(group.Id));
    }
}
