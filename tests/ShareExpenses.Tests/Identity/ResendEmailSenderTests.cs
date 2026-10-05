using System.Net;
using System.Text;
using System.Text.Json;
using ShareExpenses.Infrastructure.Identity;

namespace ShareExpenses.Tests.Identity;

/// <summary>The exact request Resend gets, and how a failure reaches the callers (spec §4).</summary>
public class ResendEmailSenderTests
{
    private const string Address = "bob@example.com";

    private sealed class FakeResend(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Received { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Received.Add((request, await request.Content!.ReadAsStringAsync(ct)));
            return await answer(request, ct);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (ResendEmailSender Sender, FakeResend Resend) SenderAnswering(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer, TimeSpan? timeout = null)
    {
        var resend = new FakeResend(answer);
        var http = new HttpClient(resend)
        {
            BaseAddress = EmailOptions.ResendApi,
            Timeout = timeout ?? EmailOptions.Timeout,
        };
        http.DefaultRequestHeaders.Authorization = new("Bearer", "re_key");
        return (new ResendEmailSender(http, new EmailOptions("Shared expenses <noreply@splitit.ftft.dk>", "re_key")), resend);
    }

    private static (ResendEmailSender Sender, FakeResend Resend) Accepting() =>
        SenderAnswering((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"id":"abc"}""")));

    [Fact]
    public async Task A_sign_in_code_is_posted_to_the_emails_endpoint_with_the_key_and_an_idempotency_key()
    {
        var (sender, resend) = Accepting();

        await sender.SendSignInAsync(Address, "123456");

        var (request, body) = Assert.Single(resend.Received);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.resend.com/emails", request.RequestUri?.ToString());
        Assert.Equal("Bearer re_key", request.Headers.Authorization?.ToString());
        Assert.True(Guid.TryParse(Assert.Single(request.Headers.GetValues("Idempotency-Key")), out _));

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("Shared expenses <noreply@splitit.ftft.dk>", root.GetProperty("from").GetString());
        Assert.Equal([Address], root.GetProperty("to").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("Your sign-in code is 123456", root.GetProperty("subject").GetString());
        Assert.StartsWith("Your sign-in code is 123456.", root.GetProperty("text").GetString());
        Assert.Contains("<strong>123456</strong>", root.GetProperty("html").GetString());
    }

    [Fact]
    public async Task An_invite_carries_the_invite_wording()
    {
        var (sender, resend) = Accepting();

        await sender.SendInviteAsync(Address, "https://splitit.ftft.dk/", "Lisbon trip", "Alice", "Bob");

        using var json = JsonDocument.Parse(Assert.Single(resend.Received).Body);
        Assert.Equal("Alice invited you to Lisbon trip on Shared expenses", json.RootElement.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Every_send_has_its_own_idempotency_key()
    {
        var (sender, resend) = Accepting();

        await sender.SendSignInAsync(Address, "123456");
        await sender.SendSignInAsync(Address, "123456");

        Assert.Equal(2, resend.Received.Select(r => r.Request.Headers.GetValues("Idempotency-Key").Single()).Distinct().Count());
    }

    [Fact]
    public async Task A_refusal_throws_with_the_status_and_resends_error_name_but_neither_address_nor_code()
    {
        var (sender, _) = SenderAnswering((_, _) => Task.FromResult(Json(HttpStatusCode.UnprocessableEntity,
            $$"""{"statusCode":422,"name":"validation_error","message":"Invalid `to` field: {{Address}}"}""")));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendSignInAsync(Address, "123456"));

        Assert.Equal("Resend answered 422 (validation_error)", failure.Message);
    }

    [Fact]
    public async Task A_quota_answer_is_recognisable_in_the_log()
    {
        var (sender, _) = SenderAnswering((_, _) => Task.FromResult(Json(HttpStatusCode.TooManyRequests,
            """{"statusCode":429,"name":"daily_quota_exceeded","message":"You have reached your daily email sending quota."}""")));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendSignInAsync(Address, "123456"));

        Assert.Equal("Resend answered 429 (daily_quota_exceeded)", failure.Message);
    }

    [Fact]
    public async Task An_answer_that_is_not_json_still_throws_without_echoing_it()
    {
        var (sender, _) = SenderAnswering((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent($"<html>{Address}</html>") }));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendSignInAsync(Address, "123456"));

        Assert.Equal("Resend answered 502 (no error name)", failure.Message);
    }

    [Fact]
    public async Task A_relay_that_does_not_answer_in_time_is_a_timeout_the_callers_log_not_a_cancellation()
    {
        var (sender, _) = SenderAnswering(
            async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new HttpResponseMessage(); },
            timeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => sender.SendSignInAsync(Address, "123456"));
    }

    [Fact]
    public async Task A_cancelled_request_stays_a_cancellation()
    {
        var (sender, _) = SenderAnswering(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new HttpResponseMessage(); });
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendSignInAsync(Address, "123456", cancelled.Token));
    }
}
