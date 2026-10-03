using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ShareExpenses.Infrastructure.Identity.Screens;
using ShareExpenses.Shared;

namespace ShareExpenses.Infrastructure.Identity;

/// <summary>The limits on sign-in codes (spec §4). Bound from the <c>SignIn</c> configuration section.</summary>
public sealed class SignInOptions
{
    public const string Section = "SignIn";

    public TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Wrong guesses a code survives, less one: the last wrong guess kills it.</summary>
    public int AttemptsPerCode { get; set; } = 5;

    /// <summary>Codes issued per address per <see cref="AddressWindow"/>; further requests send nothing.</summary>
    public int CodesPerAddress { get; set; } = 5;

    public TimeSpan AddressWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Requests to either endpoint per client IP per <see cref="IpWindow"/>.</summary>
    public int RequestsPerIp { get; set; } = 20;

    public TimeSpan IpWindow { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Sign-in by a six-digit code sent by email — the only way in (spec §4):
/// <c>POST /api/sign-in/code</c> sends one, <c>POST /api/sign-in</c> trades it for the
/// session cookie, creating the account on first use. Identity, not domain: plain
/// minimal API, outside the slices and Wolverine.
/// </summary>
public static class SignIn
{
    private const string RateLimitPolicy = "sign-in";

    /// <summary>The one answer for every failed sign-in: which part failed is nobody's business.</summary>
    public const string Failed = "the code is wrong or has expired";

    public const string InvalidAddress = "email is not a valid address";

    internal sealed record CodeRequest(string? Email);

    internal sealed record SignInRequest(string? Email, string? Code);

    internal sealed record SignedIn(UserId UserId);

    public static IServiceCollection AddSignIn(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(SignInOptions.Section).Get<SignInOptions>() ?? new SignInOptions();
        services.AddSingleton(options);
        services.AddScoped<SignInFlow>();

        // Per client IP, across both endpoints. Behind the reverse proxy this needs the
        // forwarded client address (UseForwardedHeaders) to mean anything.
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.RequestsPerIp, Window = options.IpWindow }));
        });
        return services;
    }

    public static void MapSignIn(this WebApplication app)
    {
        var signIn = app.MapGroup("/api/sign-in").RequireRateLimiting(RateLimitPolicy).AllowAnonymous();
        signIn.MapPost("/code", RequestCode).WithName("RequestSignInCode");
        signIn.MapPost("", VerifyCode).WithName("SignIn");

        // The screen: the same flow, as HTML (spec §3), under the same rate limit.
        SignInPage.Map(app.MapGroup("/sign-in").RequireRateLimiting(RateLimitPolicy).AllowAnonymous());
    }

    /// <summary>
    /// Always 202, whether or not the address has an account — and whether or not a
    /// code was actually sent: over the address's limit, nothing is. Only an address
    /// that is not plausibly one gets a 400, which reveals nothing about accounts.
    /// </summary>
    private static async Task<IResult> RequestCode(CodeRequest request, SignInFlow flow, CancellationToken ct) =>
        await flow.RequestCode(request.Email, ct)
            ? Results.Accepted()
            : Results.Problem(InvalidAddress, statusCode: StatusCodes.Status400BadRequest);

    private static async Task<IResult> VerifyCode(SignInRequest request, SignInFlow flow, CancellationToken ct) =>
        await flow.Verify(request.Email, request.Code, ct) is { } user
            ? Results.Ok(new SignedIn(user))
            : Fail();

    private static IResult Fail() => Results.Problem(Failed, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>SHA-256 of the code, as 64 lowercase hex characters.</summary>
    internal static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    internal static bool Matches(string? code, string recordedHash) =>
        code is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(code.Trim())), Encoding.ASCII.GetBytes(recordedHash));
}

/// <summary>
/// The sign-in flow itself (spec §4), behind both the JSON endpoints and the sign-in
/// screen, so the two can never differ.
/// </summary>
internal sealed class SignInFlow(
    IdentityDb db, UserManager<User> users, SignInManager<User> signIn, IEmailSender mail,
    SignInOptions options, TimeProvider clock, ILogger<SignInFlow> logger)
{
    /// <summary>
    /// Issues and emails a code, unless the address is over its limit — which the caller
    /// cannot tell, so neither limits nor accounts are disclosed.
    /// </summary>
    /// <returns>False only when the address is not plausibly one.</returns>
    public async Task<bool> RequestCode(string? email, CancellationToken ct)
    {
        var address = EmailAddress.Trim(email);
        if (!EmailAddress.IsPlausible(address))
            return false;

        var key = EmailAddress.Normalize(address);
        var now = clock.GetUtcNow();
        var row = await db.SignInCodes.FindAsync([key], ct);
        if (row is null)
        {
            row = new SignInCode { NormalizedEmail = key, WindowStartedAt = now };
            db.SignInCodes.Add(row);
        }

        if (now >= row.WindowStartedAt + options.AddressWindow)
            (row.WindowStartedAt, row.CodesIssued) = (now, 0);
        if (row.CodesIssued >= options.CodesPerAddress)
            return true;

        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
        row.CodeHash = SignIn.Hash(code);
        row.ExpiresAt = now + options.CodeLifetime;
        row.AttemptsLeft = options.AttemptsPerCode;
        row.CodesIssued++;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A simultaneous request for the same address won; its code stands.
            return true;
        }

        try
        {
            await mail.SendSignInAsync(address, code, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Never the code or the address: neither belongs in logs.
            logger.LogError(e, "A sign-in code was issued, but its email could not be sent");
        }

        return true;
    }

    /// <summary>Trades a code for the session cookie, creating the account on first use.</summary>
    /// <returns>The signed-in user, or null for every failure alike.</returns>
    public async Task<UserId?> Verify(string? email, string? code, CancellationToken ct)
    {
        var address = EmailAddress.Trim(email);
        var row = address.Length == 0 ? null : await db.SignInCodes.FindAsync([EmailAddress.Normalize(address)], ct);
        if (row?.CodeHash is null || clock.GetUtcNow() >= row.ExpiresAt || row.AttemptsLeft <= 0)
            return null;

        if (!SignIn.Matches(code, row.CodeHash))
        {
            row.AttemptsLeft--;
            if (row.AttemptsLeft == 0)
                row.CodeHash = null;
            await TrySave(ct);
            return null;
        }

        // Single use: spent before anything else happens. If a simultaneous attempt
        // changed the row first, this one fails.
        db.SignInCodes.Remove(row);
        if (!await TrySave(ct))
            return null;

        var user = await users.FindByEmailAsync(address);
        if (user is null)
        {
            // The account is created on first use: proving the code proves the address.
            user = new User { Id = Guid.CreateVersion7(), UserName = address, Email = address, EmailConfirmed = true };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
                throw new InvalidOperationException(
                    "Could not create the account: " + string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        await signIn.SignInAsync(user, isPersistent: true);
        return UserId.From(user.Id);
    }

    private async Task<bool> TrySave(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}
