using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ShareExpenses.Infrastructure.Identity;

public static class IdentitySetup
{
    public static IServiceCollection AddPasswordlessIdentity(
        this IServiceCollection services, string connectionString, IHostEnvironment env)
    {
        services.AddDbContext<IdentityDb>(o => o.UseNpgsql(connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDb.Schema)));

        // Keys must survive restarts, or every restart signs everyone out.
        services.AddDataProtection()
            .SetApplicationName("ShareExpenses")
            .PersistKeysToDbContext<IdentityDb>();

        // No token providers: sign-in codes are our own SignInCode records (spec §4).
        services
            .AddIdentityCore<User>(o => o.User.RequireUniqueEmail = true)
            .AddEntityFrameworkStores<IdentityDb>()
            .AddSignInManager();

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
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        });

        services.AddAuthorization(o =>
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddEmailSending(env);
        services.AddScoped<IEmailDirectory, IdentityEmailDirectory>();
        return services;
    }
}
