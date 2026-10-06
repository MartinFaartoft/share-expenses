using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SplitIt.Tests.Infrastructure;
using SplitIt.Web;

namespace SplitIt.Tests.Web;

/// <summary>
/// Behind Caddy every connection comes from the proxy (spec §4): the client's address, the
/// rate limiter's key, and the scheme are the forwarded ones — from a trusted proxy only.
/// </summary>
[Collection(AppCollection.Name)]
public class ForwardedHeadersTests(AppFixture app)
{
    private const string RemoteHeader = "X-Test-Remote";

    private sealed class RemoteAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use((http, continueWith) => { SetRemote(http); return continueWith(http); });
            next(builder);
        };
    }

    /// <summary>The test server has no network: the connection's address is whatever the test says.</summary>
    private static void SetRemote(HttpContext http)
    {
        if (http.Request.Headers.TryGetValue(RemoteHeader, out var address))
            http.Connection.RemoteIpAddress = IPAddress.Parse(address.ToString());
    }

    private static HttpRequestMessage Request(string path, string remote, string? forwardedFor = null, string? forwardedProto = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(RemoteHeader, remote);
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (forwardedProto is not null)
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        return request;
    }

    // ── The rules ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("172.18.0.2", "203.0.113.9", "https", "203.0.113.9|https")]
    [InlineData("172.16.0.1", "203.0.113.9", "https", "203.0.113.9|https")]
    [InlineData("172.31.255.254", "203.0.113.9", "https", "203.0.113.9|https")]
    [InlineData("10.1.2.3", "203.0.113.9", "https", "203.0.113.9|https")]
    [InlineData("192.168.1.2", "203.0.113.9", "https", "203.0.113.9|https")]
    [InlineData("172.18.0.2", "198.51.100.7, 203.0.113.9", "https", "203.0.113.9|https")]
    [InlineData("172.18.0.2", null, null, "172.18.0.2|http")]
    public async Task A_trusted_proxy_speaks_for_the_client(string remote, string? forwardedFor, string? proto, string expected)
    {
        await using var host = await MiniHost();

        var answer = await (await host.Client.SendAsync(Request("/who", remote, forwardedFor, proto))).Content.ReadAsStringAsync();

        Assert.Equal(expected, answer);
    }

    [Theory]
    [InlineData("203.0.113.5")]
    [InlineData("172.32.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("11.0.0.1")]
    public async Task Anyone_else_cannot_claim_an_address_or_a_scheme(string remote)
    {
        await using var host = await MiniHost();

        var answer = await (await host.Client.SendAsync(Request("/who", remote, "198.51.100.7", "https"))).Content.ReadAsStringAsync();

        Assert.Equal($"{remote}|http", answer);
    }

    // ── The wiring ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_sign_in_limit_counts_each_client_not_the_proxy_and_a_spoofed_header_buys_nothing()
    {
        // Its own database: a second host on the shared one disturbs the fixture host's async daemon.
        var database = await app.EmptyDatabase();
        await using var factory = app.WithWebHostBuilder(host => host
            .UseSetting("ConnectionStrings:Default", database)
            .UseSetting("SignIn:RequestsPerIp", "2")
            .ConfigureTestServices(services => services.AddSingleton<IStartupFilter, RemoteAddressFilter>()));
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        async Task<HttpStatusCode> Get(string remote, string forwardedFor) =>
            (await client.SendAsync(Request("/sign-in", remote, forwardedFor, "https"))).StatusCode;

        // Through Caddy: two clients, each with their own two requests.
        Assert.Equal(HttpStatusCode.OK, await Get("172.18.0.2", "203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, await Get("172.18.0.2", "203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Get("172.18.0.2", "203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, await Get("172.18.0.2", "203.0.113.2"));

        // Straight to the app, not through Caddy: a new made-up address each time changes nothing.
        Assert.Equal(HttpStatusCode.OK, await Get("203.0.113.50", "198.51.100.1"));
        Assert.Equal(HttpStatusCode.OK, await Get("203.0.113.50", "198.51.100.2"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Get("203.0.113.50", "198.51.100.3"));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private sealed class Host(WebApplication app) : IAsyncDisposable
    {
        public HttpClient Client { get; } = app.GetTestClient();

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    /// <summary>Only the forwarding rules, as <c>AddWeb</c> and <c>UseWebForwardedHeaders</c> configure them.</summary>
    private static async Task<Host> MiniHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWeb();
        var web = builder.Build();
        web.Use((http, next) => { SetRemote(http); return next(http); });
        web.UseWebForwardedHeaders();
        web.MapGet("/who", (HttpContext http) => $"{http.Connection.RemoteIpAddress}|{http.Request.Scheme}");
        await web.StartAsync();
        return new Host(web);
    }
}
