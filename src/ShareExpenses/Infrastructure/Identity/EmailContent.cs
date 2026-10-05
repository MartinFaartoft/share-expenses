using System.Net;

namespace ShareExpenses.Infrastructure.Identity;

/// <summary>An email as it is sent: a plain-text part as well as HTML, which is part of deliverability.</summary>
public sealed record EmailContentParts(string Subject, string Text, string Html);

/// <summary>What the emails say. Pure, so the wording is tested without a relay (spec §4).</summary>
public static class EmailContent
{
    public const string AppName = "Shared expenses";

    /// <summary>
    /// The code is in the subject and the first line: Apple's one-time-code detection
    /// looks for it there (spec §14).
    /// </summary>
    public static EmailContentParts SignIn(string code) => new(
        $"Your sign-in code is {code}",
        $"Your sign-in code is {code}.\n\nIt expires in a few minutes. If you did not ask for it, ignore this email.\n",
        Page($"<p>Your sign-in code is <strong>{Encode(code)}</strong>.</p>"
             + "<p>It expires in a few minutes. If you did not ask for it, ignore this email.</p>"));

    /// <summary>No secret: anyone may open the link, and only the invited address can sign in and join.</summary>
    public static EmailContentParts Invite(string email, string link, string groupName, string inviterName, string memberName) => new(
        $"{inviterName} invited you to {groupName} on {AppName}",
        $"{inviterName} invited you to {groupName} as {memberName}.\n\n"
        + $"Sign in with this address ({email}) to join: {link}\n",
        Page($"<p>{Encode(inviterName)} invited you to <strong>{Encode(groupName)}</strong> as {Encode(memberName)}.</p>"
             + $"<p>Sign in with this address ({Encode(email)}) to join: <a href=\"{Encode(link)}\">{Encode(link)}</a></p>"));

    private static string Page(string body) =>
        $"<!doctype html><html><body style=\"font-family: system-ui, sans-serif; line-height: 1.5\">{body}</body></html>";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
