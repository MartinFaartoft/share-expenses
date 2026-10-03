using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ShareExpenses.Web;

/// <summary>
/// The HTML side (spec §3): server-rendered Razor Components with htmx, beside the JSON
/// API. Shared by every slice's screens; not in <c>Shared/</c>, which stays
/// framework-free (spec §12).
/// </summary>
public static class WebSetup
{
    /// <summary>True for a request htmx made: answer it with a fragment, not a whole page.</summary>
    public static bool IsHtmx(this HttpRequest request) => request.Headers.ContainsKey("HX-Request");

    public static IServiceCollection AddWeb(this IServiceCollection services)
    {
        services.AddRazorComponents();
        services.AddAntiforgery();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, SignInRedirect>();
        return services;
    }

    /// <summary>Static files, before authentication: the stylesheet and htmx are public.</summary>
    public static void UseWebStaticFiles(this WebApplication app) => app.UseStaticFiles();

    /// <summary>Antiforgery for form posts, after authentication (the token is tied to the user).</summary>
    public static void UseWebAntiforgery(this WebApplication app)
    {
        app.UseAntiforgery();

        // The antiforgery middleware only records a failure. Answer it here, before an
        // endpoint reads the form — Wolverine's generated code would throw: a 500 (spec §3).
        app.Use(async (http, next) =>
        {
            if (http.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false })
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            await next(http);
        });
    }

    public static void MapWeb(this WebApplication app) =>
        app.MapGet("/", () => new RazorComponentResult<HomePage>()).WithName("Home");

    /// <summary>
    /// A screen a signed-out user asks for redirects to sign in; the JSON API keeps
    /// answering 401. Scheme-independent, so it holds whichever scheme authenticates.
    /// </summary>
    private sealed class SignInRedirect : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler _default = new();

        public Task HandleAsync(
            RequestDelegate next, HttpContext http, AuthorizationPolicy policy, PolicyAuthorizationResult result)
        {
            if (result.Challenged && !http.Request.Path.StartsWithSegments("/api"))
            {
                http.Response.Redirect("/sign-in");
                return Task.CompletedTask;
            }
            return _default.HandleAsync(next, http, policy, result);
        }
    }
}
