using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using ShareExpenses.Infrastructure.Identity;

namespace ShareExpenses.Tests.Infrastructure;

/// <summary>
/// The real application against a throwaway PostgreSQL container, one per test run.
/// Requests are authenticated by naming a user id in the <see cref="UserHeader"/>
/// header, so tests do not depend on the sign-in flow.
/// </summary>
public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string UserHeader = "X-Test-User";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public HttpClient ClientFor(ShareExpenses.Shared.UserId userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(UserHeader, userId.ToString());
        return client;
    }

    /// <summary>Every email the app sends, instead of logging it.</summary>
    public RecordingEmailSender Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(HeaderAuthentication.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>(HeaderAuthentication.SchemeName, null);
            services.AddSingleton<IEmailSender>(Emails);
        });
    }

    private sealed class HeaderAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}

[CollectionDefinition(Name)]
public sealed class AppCollection : ICollectionFixture<AppFixture>
{
    public const string Name = "app";
}
