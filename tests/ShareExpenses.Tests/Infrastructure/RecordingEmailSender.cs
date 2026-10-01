using System.Collections.Concurrent;
using ShareExpenses.Infrastructure.Identity;

namespace ShareExpenses.Tests.Infrastructure;

public sealed record SentInvite(string Email, string Link, string GroupName, string InviterName, string MemberName);

/// <summary>
/// Records emails instead of sending them. Shared by every test in the run, so look
/// emails up by something unique to the test (e.g. the link's group id).
/// </summary>
public sealed class RecordingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<SentInvite> _invites = new();

    /// <summary>When set, sending an invite throws — to test that delivery failure is not the caller's failure.</summary>
    public Func<string, bool> FailInvitesTo { get; set; } = _ => false;

    public IEnumerable<SentInvite> Invites => _invites;

    public Task SendSignInAsync(string email, string link, string code, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SendInviteAsync(
        string email, string link, string groupName, string inviterName, string memberName,
        CancellationToken ct = default)
    {
        if (FailInvitesTo(email))
            throw new InvalidOperationException($"simulated delivery failure to {email}");
        _invites.Enqueue(new SentInvite(email, link, groupName, inviterName, memberName));
        return Task.CompletedTask;
    }
}
