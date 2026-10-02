using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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

    internal sealed record CodeRequest(string? Email);

    internal sealed record SignInRequest(string? Email, string? Code);

    internal sealed record SignedIn(UserId UserId);

    public static IServiceCollection AddSignIn(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(SignInOptions.Section).Get<SignInOptions>() ?? new SignInOptions();
        services.AddSingleton(options);

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
    }

    /// <summary>
    /// Always 202, whether or not the address has an account — and whether or not a
    /// code was actually sent: over the address's limit, nothing is. Only an address
    /// that is not plausibly one gets a 400, which reveals nothing about accounts.
    /// </summary>
    private static async Task<IResult> RequestCode(
        CodeRequest request, IdentityDb db, IEmailSender email, SignInOptions options, TimeProvider clock,
        ILogger<SignInOptions> logger, CancellationToken ct)
    {
        var address = EmailAddress.Trim(request.Email);
        if (!EmailAddress.IsPlausible(address))
            return Results.Problem("email is not a valid address", statusCode: StatusCodes.Status400BadRequest);

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
            return Results.Accepted();

        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
        row.CodeHash = Hash(code);
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
            return Results.Accepted();
        }

        try
        {
            await email.SendSignInAsync(address, code, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Never the code or the address: neither belongs in logs.
            logger.LogError(e, "A sign-in code was issued, but its email could not be sent");
        }

        return Results.Accepted();
    }

    private static async Task<IResult> VerifyCode(
        SignInRequest request, IdentityDb db, UserManager<User> users, SignInManager<User> signIn,
        SignInOptions options, TimeProvider clock, CancellationToken ct)
    {
        var address = EmailAddress.Trim(request.Email);
        var row = address.Length == 0 ? null : await db.SignInCodes.FindAsync([EmailAddress.Normalize(address)], ct);
        if (row?.CodeHash is null || clock.GetUtcNow() >= row.ExpiresAt || row.AttemptsLeft <= 0)
            return Fail();

        if (!Matches(request.Code, row.CodeHash))
        {
            row.AttemptsLeft--;
            if (row.AttemptsLeft == 0)
                row.CodeHash = null;
            await TrySave(db, ct);
            return Fail();
        }

        // Single use: spent before anything else happens. If a simultaneous attempt
        // changed the row first, this one fails.
        db.SignInCodes.Remove(row);
        if (!await TrySave(db, ct))
            return Fail();

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
        return Results.Ok(new SignedIn(UserId.From(user.Id)));
    }

    private static IResult Fail() => Results.Problem(Failed, statusCode: StatusCodes.Status400BadRequest);

    private static async Task<bool> TrySave(IdentityDb db, CancellationToken ct)
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

    /// <summary>SHA-256 of the code, as 64 lowercase hex characters.</summary>
    internal static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static bool Matches(string? code, string recordedHash) =>
        code is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(code.Trim())), Encoding.ASCII.GetBytes(recordedHash));
}
