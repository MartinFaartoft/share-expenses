namespace ShareExpenses.Infrastructure.Identity;

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
