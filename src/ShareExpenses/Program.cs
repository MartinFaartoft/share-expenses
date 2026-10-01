using Marten;
using ShareExpenses;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Infrastructure.Marten;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing connection string 'Postgres'.");

builder.Services.AddLedgerStore(connectionString, builder.Environment);
builder.Services.AddPasswordlessIdentity(connectionString, builder.Environment);

var app = builder.Build();

await app.MigrateIdentityInDevelopmentAsync();

app.UseAuthentication();
app.UseAuthorization();

AllSlices.Map(app.MapGroup("/api"));

app.MapGet("/health", async (IQuerySession marten, IdentityDb identity, CancellationToken ct) =>
{
    var martenOk = await marten.QueryAsync<int>("select 1", ct) is [1];
    var identityOk = await identity.Database.CanConnectAsync(ct);
    return martenOk && identityOk
        ? Results.Ok(new { status = "healthy" })
        : Results.Problem("database unreachable", statusCode: 503);
});

app.Run();

public partial class Program;
