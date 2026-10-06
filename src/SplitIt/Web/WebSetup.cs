using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.HttpOverrides;

namespace SplitIt.Web;

/// <summary>
/// The HTML side (spec §3): server-rendered Razor Components with htmx, beside the JSON
/// API. Shared by every slice's screens; not in <c>Shared/</c>, which stays
/// framework-free (spec §12).
/// </summary>
public static class WebSetup
{
    /// <summary>
    /// <paramref name="url"/> if it is a path on this site, otherwise home. Guards every
    /// redirect to an address that came from a request (<c>returnUrl</c>): anything else
    /// would let a crafted link send a just-signed-in user to another site.
    /// </summary>
    public static string LocalOrHome(string? url) =>
        url is { Length: > 0 }
        && url[0] == '/'
        && !url.StartsWith("//", StringComparison.Ordinal)          // //evil.example: another host
        && !url.StartsWith("/\\", StringComparison.Ordinal)        // /\evil.example: the same, to browsers
        && !url.Any(char.IsControl)
            ? url
            : "/";

    /// <summary>True for a request htmx made: answer it with a fragment, not a whole page.</summary>
    public static bool IsHtmx(this HttpRequest request) => request.Headers.ContainsKey("HX-Request");

    public static IServiceCollection AddWeb(this IServiceCollection services)
    {
        services.AddRazorComponents();
        services.AddAntiforgery();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, SignInRedirect>();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            // The default trusts loopback only. Caddy reaches the app over a docker bridge network
            // whose range docker picks, and nothing else can reach the app, so trust the private
            // ranges — never every address, or any client could claim to be anyone.
            foreach (var (address, prefix) in PrivateRanges)
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse(address), prefix));
        });
        return services;
    }

    private static readonly (string Address, int Prefix)[] PrivateRanges =
        [("10.0.0.0", 8), ("172.16.0.0", 12), ("192.168.0.0", 16)];

    /// <summary>
    /// First in the pipeline: behind the reverse proxy the connection is always the proxy's, so
    /// the client address (the rate limiter's key) and the scheme are the forwarded ones.
    /// </summary>
    public static void UseWebForwardedHeaders(this WebApplication app) => app.UseForwardedHeaders();

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

    /// <summary>
    /// A screen a signed-out user asks for redirects to sign in, and back. Scheme-independent,
    /// so it holds whichever scheme authenticates.
    /// </summary>
    private sealed class SignInRedirect : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler _default = new();

        public Task HandleAsync(
            RequestDelegate next, HttpContext http, AuthorizationPolicy policy, PolicyAuthorizationResult result)
        {
            if (result.Challenged)
            {
                // Back here after signing in; home, the default, needs no returnUrl.
                var here = http.Request.Path + http.Request.QueryString;
                http.Response.Redirect(here == "/" ? "/sign-in" : $"/sign-in?returnUrl={Uri.EscapeDataString(here)}");
                return Task.CompletedTask;
            }
            return _default.HandleAsync(next, http, policy, result);
        }
    }
}
