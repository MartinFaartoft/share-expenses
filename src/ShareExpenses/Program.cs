using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShareExpenses.Infrastructure;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Infrastructure.Marten;
using ShareExpenses.Infrastructure.Wolverine;
using ShareExpenses.Web;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing connection string 'Postgres'.");

builder.Services.AddLedgerStore(connectionString, builder.Environment);
builder.Services.AddPasswordlessIdentity(connectionString, builder.Environment);
builder.Services.AddPublicOrigin(builder.Configuration, builder.Environment);
builder.Services.AddSignIn(builder.Configuration);
builder.AddWolverineEndpoints();
builder.Services.AddWeb();

// The clock, injected so time-dependent decisions (invite deadlines) stay testable.
builder.Services.TryAddSingleton(TimeProvider.System);

var app = builder.Build();

await app.MigrateIdentityInDevelopmentAsync();

app.UseWebStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseWebAntiforgery();
app.UseRateLimiter();

// Slice endpoints: discovered by Wolverine, under /api. To list them:
//   dotnet run --project src/ShareExpenses -- describe
app.MapWolverineEndpoints();
app.MapSignIn();
app.MapWeb();

app.MapGet("/health", async (IQuerySession marten, IdentityDb identity, CancellationToken ct) =>
{
    var martenOk = await marten.QueryAsync<int>("select 1", ct) is [1];
    var identityOk = await identity.Database.CanConnectAsync(ct);
    return martenOk && identityOk
        ? Results.Ok(new { status = "healthy" })
        : Results.Problem("database unreachable", statusCode: 503);
}).AllowAnonymous();

// JasperFx's command line; plain "dotnet run" starts the app as before.
return await app.RunJasperFxCommands(args);

public partial class Program;
