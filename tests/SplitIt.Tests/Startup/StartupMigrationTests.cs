using System.Net;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using SplitIt.Shared;
using SplitIt.Tests.Infrastructure;

namespace SplitIt.Tests.Startup;

/// <summary>
/// The app applies its own schema on startup (spec §3): booted outside Development on an
/// empty database, as on the VPS, it comes up ready to use.
/// </summary>
[Collection(AppCollection.Name)]
public class StartupMigrationTests(AppFixture app)
{
    [Fact]
    public async Task An_empty_database_is_migrated_at_startup_and_a_group_can_be_created()
    {
        var connectionString = await app.EmptyDatabase();
        await using var factory = app.WithWebHostBuilder(host => host
            .UseEnvironment("Staging")
            .UseSetting("ConnectionStrings:Default", connectionString)
            .UseSetting("App:PublicOrigin", "https://example.test"));
        var alice = UserId.New();
        var browser = factory.CreateClient(new() { AllowAutoRedirect = false });
        browser.DefaultRequestHeaders.Add(AppFixture.UserHeader, alice.ToString());

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/healthz")).StatusCode);
        var form = await (await browser.GetAsync("/groups/new")).Content.ReadAsStringAsync();
        var created = await Forms.Post(browser, "/groups", Forms.TokenIn(form)!,
            ("groupName", "Lisbon trip"), ("currency", "GBP"), ("memberName", "Alice"));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var group = await browser.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, group.StatusCode);
        Assert.Contains("Lisbon trip", await group.Content.ReadAsStringAsync());

        Assert.Contains("ledger", await SchemasIn(connectionString));
        Assert.Contains("identity", await SchemasIn(connectionString));
    }

    private static async Task<List<string>> SchemasIn(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select distinct table_schema from information_schema.tables", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var schemas = new List<string>();
        while (await reader.ReadAsync())
            schemas.Add(reader.GetString(0));
        return schemas;
    }
}
