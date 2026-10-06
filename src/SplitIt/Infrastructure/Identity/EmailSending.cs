namespace SplitIt.Infrastructure.Identity;

/// <summary>
/// Outgoing email: sign-in codes (spec §4), and invites (InviteMember) — which carry
/// no secret, only a link to the app and the address to sign in with.
/// </summary>
public interface IEmailSender
{
    Task SendSignInAsync(string email, string code, CancellationToken ct = default);

    Task SendInviteAsync(
        string email, string link, string groupName, string inviterName, string memberName,
        CancellationToken ct = default);
}

/// <summary>
/// Development stand-in for a real mail relay: writes each email's link to the
/// application log — the sign-in code included, which is fine for a development
/// stand-in and nowhere else. Must never run in Production — there it would "work"
/// perfectly while silently locking out every real user.
/// </summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendSignInAsync(string email, string code, CancellationToken ct = default)
    {
        logger.LogWarning("Sign-in email for {Email}: code {Code}", email, code);
        return Task.CompletedTask;
    }

    public Task SendInviteAsync(
        string email, string link, string groupName, string inviterName, string memberName,
        CancellationToken ct = default)
    {
        logger.LogWarning("Invite email for {Email}: {Inviter} invited you to {Group} as {Member}; sign in at {Link}",
            email, inviterName, groupName, memberName, link);
        return Task.CompletedTask;
    }
}

public static class EmailSendingRegistration
{
    public const string ApiKeyKey = "Email:Resend:ApiKey";
    public const string FromKey = "Email:From";

    /// <summary>
    /// A key means Resend, in any environment. No key means the log sender, except in
    /// Production: there it would "work" perfectly while silently locking out every real
    /// user, so the app refuses to start (spec §4).
    /// </summary>
    public static IServiceCollection AddEmailSending(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        var apiKey = configuration[ApiKeyKey];
        var from = configuration[FromKey];

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (env.IsProduction())
            {
                throw new InvalidOperationException(
                    $"No mail relay is configured ({ApiKeyKey}), and LogEmailSender refuses to run in Production: " +
                    "it would silently lock out every user (spec §4).");
            }

            return services.AddSingleton<IEmailSender, LogEmailSender>();
        }

        if (!System.Net.Mail.MailAddress.TryCreate(from, out _))
        {
            throw new InvalidOperationException(
                $"{FromKey} must be an address, e.g. noreply@example.com — got '{from}'.");
        }

        var options = new EmailOptions(from!, apiKey.Trim());
        services.AddHttpClient<IEmailSender, ResendEmailSender>(http =>
        {
            http.BaseAddress = EmailOptions.ResendApi;
            http.Timeout = EmailOptions.Timeout;
            http.DefaultRequestHeaders.Authorization = new("Bearer", options.ApiKey);
        });
        services.AddSingleton(options);
        return services;
    }
}
