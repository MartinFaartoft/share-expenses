using System.Security.Claims;
using System.Text.Encodings.Web;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
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

    public async Task InitializeAsync()
    {
        // Program ends in RunJasperFxCommands, which otherwise never starts the host
        // under a test server.
        JasperFx.CommandLine.JasperFxEnvironment.AutoStartHost = true;
        await _postgres.StartAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>
    /// An account for <paramref name="email"/>, as signing in with a code would create
    /// it — so its address counts as verified (spec §4). Tests then act as it with
    /// <see cref="ClientFor"/>, without going through sign-in.
    /// </summary>
    public async Task<ShareExpenses.Shared.UserId> AccountFor(string email)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = new User { Id = Guid.CreateVersion7(), UserName = email, Email = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        return ShareExpenses.Shared.UserId.From(user.Id);
    }

    /// <summary>
    /// Waits until Marten's async daemon has projected every event appended so far
    /// (spec §11): read after this, an asynchronous projection is up to date.
    /// </summary>
    public Task ProjectionsCaughtUp() =>
        Marten.Events.TestingExtensions.WaitForNonStaleProjectionDataAsync(
            Services.GetRequiredService<Marten.IDocumentStore>(), TimeSpan.FromSeconds(15));

    /// <summary>
    /// A browser signed in as <paramref name="userId"/>: keeps cookies (the antiforgery
    /// one included) and does not follow redirects, so tests see where they point.
    /// </summary>
    public HttpClient BrowserFor(ShareExpenses.Shared.UserId userId)
    {
        var browser = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        browser.DefaultRequestHeaders.Add(UserHeader, userId.ToString());
        return browser;
    }

    public HttpClient ClientFor(ShareExpenses.Shared.UserId userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(UserHeader, userId.ToString());
        return client;
    }

    /// <summary>A connection string to a new, empty database on the test container.</summary>
    public async Task<string> EmptyDatabase()
    {
        var name = $"empty_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"create database {name}", admin);
            await create.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
    }

    /// <summary>Every email the app sends, instead of logging it.</summary>
    public RecordingEmailSender Emails { get; } = new();

    /// <summary>
    /// The app's clock. Starts at the real time; a test that moves it must restore it
    /// (the fixture is shared by every test in the run).
    /// </summary>
    public TestClock Clock { get; } = new();

    /// <summary>Injects a competing write into the app's next save (concurrency tests).</summary>
    public BeforeNextSave BeforeNextSave { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        // Every test request comes from the same (absent) client IP: lift that limit;
        // the sign-in tests check the per-address limits instead.
        builder.UseSetting("SignIn:RequestsPerIp", "100000");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(HeaderAuthentication.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>(HeaderAuthentication.SchemeName, null);
            services.AddSingleton<IEmailSender>(Emails);
            services.AddSingleton<TimeProvider>(Clock);
            services.ConfigureMarten(opts => opts.Listeners.Add(BeforeNextSave));
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
