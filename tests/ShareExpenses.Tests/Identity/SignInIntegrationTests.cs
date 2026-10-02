using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;
using ShareExpenses.Tests.Infrastructure;

namespace ShareExpenses.Tests.Identity;

/// <summary>Sign-in by a six-digit code sent by email — the only way in (spec §4).</summary>
[Collection(AppCollection.Name)]
public class SignInIntegrationTests(AppFixture app)
{
    private readonly string _email = $"someone-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task A_code_signs_in_creating_the_account_on_first_use()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await RequestCode(_email)).StatusCode);
        Assert.Null(await AccountFor(_email)); // nothing written for the address until its code is used

        var response = await SignIn(_email, CodeFor(_email));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Identity.Application="));
        var signedIn = await response.Content.ReadFromJsonAsync<SignedInBody>();
        Assert.Equal(await AccountFor(_email), signedIn!.UserId);
    }

    [Fact]
    public async Task A_code_signs_in_an_existing_account_as_itself()
    {
        var existing = await app.AccountFor(_email);
        await RequestCode(_email.ToUpperInvariant());

        var response = await SignIn(_email, CodeFor(_email.ToUpperInvariant()));

        Assert.Equal(existing, (await response.Content.ReadFromJsonAsync<SignedInBody>())!.UserId);
    }

    [Fact]
    public async Task The_code_is_six_digits()
    {
        await RequestCode(_email);

        Assert.Matches("^[0-9]{6}$", CodeFor(_email));
    }

    [Fact]
    public async Task A_code_works_once()
    {
        await RequestCode(_email);
        var code = CodeFor(_email);
        Assert.Equal(HttpStatusCode.OK, (await SignIn(_email, code)).StatusCode);

        await AssertFailed(await SignIn(_email, code));
    }

    [Fact]
    public async Task A_code_dies_after_its_lifetime()
    {
        await RequestCode(_email);
        try
        {
            app.Clock.Offset = TimeSpan.FromMinutes(10);
            await AssertFailed(await SignIn(_email, CodeFor(_email)));
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task Five_wrong_guesses_kill_the_code()
    {
        await RequestCode(_email);
        var code = CodeFor(_email);
        var wrong = code == "000000" ? "000001" : "000000";

        for (var i = 0; i < 5; i++)
            await AssertFailed(await SignIn(_email, wrong));

        await AssertFailed(await SignIn(_email, code));
    }

    [Fact]
    public async Task Four_wrong_guesses_leave_the_code_usable()
    {
        await RequestCode(_email);
        var code = CodeFor(_email);
        var wrong = code == "000000" ? "000001" : "000000";

        for (var i = 0; i < 4; i++)
            await AssertFailed(await SignIn(_email, wrong));

        Assert.Equal(HttpStatusCode.OK, (await SignIn(_email, code)).StatusCode);
    }

    [Fact]
    public async Task A_new_code_replaces_the_previous_one()
    {
        await RequestCode(_email);
        var first = CodeFor(_email);
        await RequestCode(_email);
        var second = CodeFor(_email);

        if (first != second) // one in a million they coincide
            await AssertFailed(await SignIn(_email, first));
        Assert.Equal(HttpStatusCode.OK, (await SignIn(_email, second)).StatusCode);
    }

    [Fact]
    public async Task An_address_gets_at_most_five_codes_an_hour_and_cannot_tell()
    {
        for (var i = 0; i < 5; i++)
            await RequestCode(_email);
        var sent = app.Emails.Codes.Count(c => c.Email == _email);

        var sixth = await RequestCode(_email);

        Assert.Equal(HttpStatusCode.Accepted, sixth.StatusCode);
        Assert.Equal(5, sent);
        Assert.Equal(5, app.Emails.Codes.Count(c => c.Email == _email));
    }

    [Fact]
    public async Task Using_up_a_codes_attempts_does_not_reset_the_hourly_limit()
    {
        for (var i = 0; i < 5; i++)
        {
            await RequestCode(_email);
            for (var j = 0; j < 5; j++)
                await SignIn(_email, "xxxxxx");
        }

        await RequestCode(_email);

        Assert.Equal(5, app.Emails.Codes.Count(c => c.Email == _email));
    }

    [Fact]
    public async Task Every_failure_is_the_same_answer()
    {
        await RequestCode(_email);

        HttpResponseMessage[] failed =
        [
            await SignIn(_email, "xxxxxx"),                                  // wrong code
            await SignIn($"nobody-{Guid.NewGuid():N}@example.com", "123456"), // no code for this address
            await SignIn("", "123456"),                                      // no address
            await SignIn(_email, null),                                      // no code
        ];

        foreach (var response in failed)
            await AssertFailed(response);
        Assert.Single((await Task.WhenAll(failed.Select(r => r.Content.ReadAsStringAsync()))).Distinct());
    }

    [Fact]
    public async Task Requesting_a_code_for_an_implausible_address_is_400()
    {
        var response = await RequestCode("not-an-email");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("email is not a valid address", await response.Content.ReadAsStringAsync());
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> RequestCode(string email) =>
        app.CreateClient().PostAsJsonAsync("/api/sign-in/code", new { email });

    private Task<HttpResponseMessage> SignIn(string email, string? code) =>
        app.CreateClient().PostAsJsonAsync("/api/sign-in", new { email, code });

    private string CodeFor(string email) =>
        app.Emails.LatestCodeFor(email) ?? throw new InvalidOperationException($"No code was sent to {email}");

    private async Task<UserId?> AccountFor(string email)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IEmailDirectory>().AccountFor(email);
    }

    private static async Task AssertFailed(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(ShareExpenses.Infrastructure.Identity.SignIn.Failed, await response.Content.ReadAsStringAsync());
    }

    private sealed record SignedInBody(UserId UserId);
}
