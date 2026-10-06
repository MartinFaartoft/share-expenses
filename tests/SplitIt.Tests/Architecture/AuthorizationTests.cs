using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SplitIt.Tests.Infrastructure;

namespace SplitIt.Tests.Architecture;

/// <summary>
/// Secure by default (spec §4). A fallback policy makes every endpoint without
/// authorization metadata require a signed-in user, so a new endpoint that forgets
/// <c>[Authorize]</c> is safe rather than silently public. What remains to guard is
/// the allow-list: the endpoints marked <c>[AllowAnonymous]</c> / <c>.AllowAnonymous()</c>.
/// They are read from the running app's <see cref="EndpointDataSource"/>, which holds
/// Wolverine's endpoints and minimal APIs alike, so both ways of declaring one count.
/// </summary>
[Collection(AppCollection.Name)]
public class AuthorizationTests(AppFixture app)
{
    private static readonly string[] AllowList =
    [
        "GET /healthz",
        "GET /sign-in",
        "POST /sign-in",
        "POST /sign-in/code",
    ];

    /// <summary>
    /// Exact equality: fails when an unexpected endpoint is anonymous, and when an
    /// allow-listed one is gone or no longer anonymous — so the list cannot rot.
    /// </summary>
    [Fact]
    public void Only_the_allow_listed_endpoints_are_anonymous()
    {
        var anonymous = Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .SelectMany(Names)
            .Order(StringComparer.Ordinal);

        Assert.Equal(AllowList, anonymous);
    }

    [Fact]
    public void The_fallback_policy_requires_a_signed_in_user()
    {
        var fallback = app.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy;

        Assert.NotNull(fallback);
        Assert.Contains(fallback.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task An_unauthenticated_request_is_sent_to_sign_in()
    {
        // Screens answer it by redirecting to sign in, and back.
        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/groups/x/balances");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in?returnUrl=%2Fgroups%2Fx%2Fbalances", response.Headers.Location?.OriginalString);
    }

    private IEnumerable<RouteEndpoint> Endpoints()
    {
        // Resolving services builds and starts the host, so MapWolverineEndpoints has run.
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        // Wolverine's endpoints are among them (Join is one), or this test would pass vacuously.
        Assert.Contains("POST /invites/{group}/join", endpoints.SelectMany(Names));
        return endpoints;
    }

    private static IEnumerable<string> Names(RouteEndpoint endpoint)
    {
        // Slashes normalised: MapPost("") on a group leaves a trailing one
        // (/sign-in/), which routing ignores; a leading one is not guaranteed.
        var route = "/" + endpoint.RoutePattern.RawText?.Trim('/');
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
        return methods.Select(m => $"{m} {route}");
    }
}
