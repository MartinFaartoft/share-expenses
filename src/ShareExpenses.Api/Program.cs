using JasperFx;
using Marten;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ShareExpenses.Api.Identity;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing connection string 'Postgres'.");

// ── Marten: event store + projected documents (spec §3, §11) ────────────────
builder.Services
    .AddMarten(opts =>
    {
        opts.Connection(connectionString);
        opts.DatabaseSchemaName = "ledger";
        opts.Events.DatabaseSchemaName = "ledger";

        // Development creates/patches schema objects on the fly. Anywhere else the
        // schema must already exist — apply changes deliberately, not at runtime.
        opts.AutoCreateSchemaObjects = builder.Environment.IsDevelopment()
            ? AutoCreate.CreateOrUpdate
            : AutoCreate.None;

        // Event types, inline projections and the async daemon are registered here
        // as slices are implemented (GroupLedger, ActivityFeed inline; UserGroups async).
    })
    .UseLightweightSessions();

// ── EF Core, confined to identity (spec §4) ─────────────────────────────────
builder.Services.AddDbContext<IdentityDb>(o => o.UseNpgsql(connectionString,
    npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDb.Schema)));

// Keys must survive restarts, or every restart invalidates every sign-in link and session.
builder.Services.AddDataProtection()
    .SetApplicationName("ShareExpenses")
    .PersistKeysToDbContext<IdentityDb>();

builder.Services.Configure<PasswordlessLoginTokenProviderOptions>(_ => { });
builder.Services
    .AddIdentityCore<IdentityUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<IdentityDb>()
    .AddSignInManager()
    .AddDefaultTokenProviders() // includes EmailTokenProvider for the six-digit code
    .AddTokenProvider<PasswordlessLoginTokenProvider>(PasswordlessLoginTokenProvider.ProviderName);

builder.Services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Lax;
    // An API answers with status codes, not redirects to a login page.
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();

builder.Services.AddEmailSending(builder.Environment);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<IdentityDb>().Database.MigrateAsync();
}

app.UseAuthentication();
app.UseAuthorization();

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
