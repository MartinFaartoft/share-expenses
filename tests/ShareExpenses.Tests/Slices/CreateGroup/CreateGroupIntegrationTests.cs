using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using ShareExpenses.Slices.CreateGroup;
using ShareExpenses.Tests.Infrastructure;
using ShareExpenses.Tests.Specs;

namespace ShareExpenses.Tests.Slices.CreateGroup;

[Collection(AppCollection.Name)]
public class CreateGroupIntegrationTests(AppFixture app)
{
    private readonly Guid _alice = Guid.CreateVersion7();

    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    private async Task<IReadOnlyList<object>> StreamOf(Guid groupId)
    {
        await using var session = Store.QuerySession();
        return (await session.Events.FetchStreamAsync(groupId)).Select(e => e.Data).ToList();
    }

    // Fresh ids per test: the container is shared by every test in the run.
    private readonly Guid _g1 = Guid.CreateVersion7();
    private readonly Guid _m1 = Guid.CreateVersion7();

    private StreamSpec<Command> Spec => new(Store, _g1, async (session, command) =>
        await Handler.Handle(session, command, _g1, _m1, default) switch
        {
            Outcome.Created => null,
            Outcome.Invalid i => i.Reason,
            Outcome.AlreadyExists => "group already exists",
            var other => throw new InvalidOperationException($"Unhandled outcome {other}"),
        });

    [Fact]
    public Task S1_creates_the_group_and_its_first_member() =>
        Spec.Given()
            .When(new Command("Lisbon trip", "GBP", "Alice", _alice))
            .Then(
                new GroupCreated(_g1, "Lisbon trip", "GBP", _alice),
                new MemberAdded(_m1, "Alice"),
                new MemberClaimed(_m1, _alice));

    [Fact]
    public Task S4_a_group_is_created_exactly_once() =>
        Spec.Given(new GroupCreated(_g1, "Lisbon trip", "GBP", _alice))
            .When(new Command("Lisbon trip", "GBP", "Alice", _alice))
            .ThenRejected("group already exists");

    [Fact]
    public async Task Post_creates_the_group_and_records_the_three_events()
    {
        var response = await app.ClientFor(_alice).PostAsJsonAsync("/api/groups",
            new { name = "Lisbon trip", currency = "gbp", displayName = "Alice" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreatedBody>();
        Assert.NotNull(body);
        Assert.Equal($"/api/groups/{body.GroupId}", response.Headers.Location?.OriginalString);

        Assert.Equal(
            [
                new GroupCreated(body.GroupId, "Lisbon trip", "GBP", _alice),
                new MemberAdded(body.MemberId, "Alice"),
                new MemberClaimed(body.MemberId, _alice),
            ],
            await StreamOf(body.GroupId));
    }

    [Fact]
    public async Task Post_is_stored_under_stable_event_type_names()
    {
        var response = await app.ClientFor(_alice).PostAsJsonAsync("/api/groups",
            new { name = "Porto", currency = "EUR", displayName = "Alice" });
        var body = await response.Content.ReadFromJsonAsync<CreatedBody>();

        await using var session = Store.QuerySession();
        var names = (await session.Events.FetchStreamAsync(body!.GroupId)).Select(e => e.EventTypeName);
        Assert.Equal(["group_created", "member_added", "member_claimed"], names);
    }

    [Fact]
    public async Task Post_rejects_an_invalid_command_with_400_and_the_reason()
    {
        var response = await app.ClientFor(_alice).PostAsJsonAsync("/api/groups",
            new { name = "Lisbon trip", currency = "XYZ", displayName = "Alice" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("currency must be a known ISO 4217 code", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_requires_a_signed_in_user()
    {
        var response = await app.CreateClient().PostAsJsonAsync("/api/groups",
            new { name = "Lisbon trip", currency = "GBP", displayName = "Alice" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record CreatedBody(Guid GroupId, Guid MemberId);
}
