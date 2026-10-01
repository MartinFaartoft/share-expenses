namespace ShareExpenses.Infrastructure.Identity;

/// <summary>
/// Outgoing email. Every sign-in email carries a tappable link and a six-digit code
/// (spec §4); every invite email carries the invite link (slice 3).
/// </summary>
public interface IEmailSender
{
    Task SendSignInAsync(string email, string link, string code, CancellationToken ct = default);

    Task SendInviteAsync(
        string email, string link, string groupName, string inviterName, string memberName,
        CancellationToken ct = default);
}

/// <summary>
/// Development stand-in for a real mail relay: writes each email's link to the
/// application log. Must never run in Production — there it would "work"
/// perfectly while silently locking out every real user.
/// </summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendSignInAsync(string email, string link, string code, CancellationToken ct = default)
    {
        logger.LogWarning("Sign-in email for {Email}: link {Link} code {Code}", email, link, code);
        return Task.CompletedTask;
    }

    public Task SendInviteAsync(
        string email, string link, string groupName, string inviterName, string memberName,
        CancellationToken ct = default)
    {
        logger.LogWarning("Invite email for {Email}: {Inviter} invited you to {Group} as {Member}: {Link}",
            email, inviterName, groupName, memberName, link);
        return Task.CompletedTask;
    }
}

public static class EmailSendingRegistration
{
    public static IServiceCollection AddEmailSending(this IServiceCollection services, IHostEnvironment env)
    {
        // Safety gate (spec §4): choosing a relay is a release blocker for Production.
        if (env.IsProduction())
        {
            throw new InvalidOperationException(
                "No mail relay is configured, and LogEmailSender refuses to run in Production: " +
                "it would silently lock out every user. Choose a relay before deploying (spec §4).");
        }

        return services.AddSingleton<IEmailSender, LogEmailSender>();
    }
}
