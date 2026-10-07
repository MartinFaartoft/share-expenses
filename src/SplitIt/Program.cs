using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SplitIt.Infrastructure;
using SplitIt.Infrastructure.Identity;
using SplitIt.Infrastructure.Marten;
using SplitIt.Infrastructure.Wolverine;
using SplitIt.Web;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing connection string 'Default'.");

builder.Services.AddLedgerStore(connectionString);
builder.Services.AddPasswordlessIdentity(connectionString, builder.Environment);
builder.Services.AddEmailSending(builder.Configuration, builder.Environment);
builder.Services.AddPublicOrigin(builder.Configuration, builder.Environment);
builder.Services.AddSignIn(builder.Configuration);
builder.AddWolverineEndpoints();
builder.Services.AddWeb();

// The clock, injected so time-dependent decisions (invite deadlines) stay testable.
builder.Services.TryAddSingleton(TimeProvider.System);

// Metrics, traces and logs to OTLP; the endpoint comes from OTEL_EXPORTER_OTLP_*.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: builder.Environment.ApplicationName,
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter("Npgsql", "Wolverine", "Marten")
        .AddOtlpExporter())
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("Npgsql", "Wolverine", "Marten")
        .AddOtlpExporter());

builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
    o.AddOtlpExporter();
});

var app = builder.Build();

await app.MigrateDatabaseAsync();

app.UseWebForwardedHeaders();
app.UseWebStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseWebAntiforgery();
app.UseRateLimiter();

// Slice endpoints, discovered by Wolverine: the screens, every route written in
// full. To list them:
//   dotnet run --project src/SplitIt -- describe
app.MapWolverineEndpoints();
app.MapSignIn();

app.MapGet("/healthz", async (IQuerySession marten, IdentityDb identity, CancellationToken ct) =>
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
