using System.Net.Http.Json;
using System.Text.Json;

namespace SplitIt.Infrastructure.Identity;

/// <summary>Where the mail goes from, and the key to send it (<c>Email:</c> in configuration; spec §4).</summary>
public sealed record EmailOptions(string From, string ApiKey)
{
    public static readonly Uri ResendApi = new("https://api.resend.com/");

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Sends through Resend's HTTP API (spec §4). Best-effort, as the callers treat it:
/// a failure throws, and they log it. The message names the status and Resend's error
/// name only — never the address or the code, nor Resend's text, which may echo them.
/// </summary>
public sealed class ResendEmailSender(HttpClient http, EmailOptions options) : IEmailSender
{
    public Task SendSignInAsync(string email, string code, CancellationToken ct = default) =>
        SendAsync(email, EmailContent.SignIn(code), ct);

    public Task SendInviteAsync(
        string email, string link, string groupName, string inviterName, string memberName,
        CancellationToken ct = default) =>
        SendAsync(email, EmailContent.Invite(email, link, groupName, inviterName, memberName), ct);

    private async Task SendAsync(string to, EmailContentParts content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(new
            {
                from = options.From,
                to = new[] { to },
                subject = content.Subject,
                text = content.Text,
                html = content.Html,
            }),
        };
        // One key per send, reused by any retry of this request.
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The client's own timeout: callers treat it as any delivery failure.
            throw new TimeoutException($"Resend did not answer within {EmailOptions.Timeout.TotalSeconds:0} seconds");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return;
            throw new InvalidOperationException(
                $"Resend answered {(int)response.StatusCode} ({await ErrorNameOf(response, ct)})");
        }
    }

    private static async Task<string> ErrorNameOf(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return body.RootElement.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } text
                ? text
                : "no error name";
        }
        catch (JsonException)
        {
            return "no error name";
        }
    }
}
