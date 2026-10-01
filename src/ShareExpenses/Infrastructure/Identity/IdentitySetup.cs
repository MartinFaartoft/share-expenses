using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ShareExpenses.Infrastructure.Identity;

/// <summary>Magic-link identity on ASP.NET Core Identity, EF Core confined to it (spec §4).</summary>
public static class IdentitySetup
{
    public static IServiceCollection AddPasswordlessIdentity(
        this IServiceCollection services, string connectionString, IHostEnvironment env)
    {
        services.AddDbContext<IdentityDb>(o => o.UseNpgsql(connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDb.Schema)));

        // Keys must survive restarts, or every restart invalidates every sign-in link and session.
        services.AddDataProtection()
            .SetApplicationName("ShareExpenses")
            .PersistKeysToDbContext<IdentityDb>();

        services.Configure<PasswordlessLoginTokenProviderOptions>(_ => { });
        services
            .AddIdentityCore<IdentityUser>(o => o.User.RequireUniqueEmail = true)
            .AddEntityFrameworkStores<IdentityDb>()
            .AddSignInManager()
            .AddDefaultTokenProviders() // includes EmailTokenProvider for the six-digit code
            .AddTokenProvider<PasswordlessLoginTokenProvider>(PasswordlessLoginTokenProvider.ProviderName);

        services
            .AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddIdentityCookies();

        services.ConfigureApplicationCookie(o =>
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
        services.AddAuthorization();

        services.AddEmailSending(env);
        return services;
    }

    /// <summary>Development only: anywhere else, migrations are applied deliberately.</summary>
    public static async Task MigrateIdentityInDevelopmentAsync(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityDb>().Database.MigrateAsync();
    }
}
