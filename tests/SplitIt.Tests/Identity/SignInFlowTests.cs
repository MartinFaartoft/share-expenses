using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SplitIt.Infrastructure.Identity;
using SplitIt.Shared;
using SplitIt.Tests.Infrastructure;

namespace SplitIt.Tests.Identity;

/// <summary>
/// Sign-in by a six-digit code sent by email — the only way in (spec §4) — through
/// the sign-in screen's plain form posts. How the screen looks and swaps is
/// <see cref="SignInScreenTests"/>; this is what the flow allows.
/// </summary>
[Collection(AppCollection.Name)]
public class SignInFlowTests(AppFixture app)
{
    private readonly string _email = $"someone-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task A_code_signs_in_creating_the_account_on_first_use()
    {
        await RequestCode(_email);
        Assert.Null(await AccountFor(_email)); // nothing written for the address until its code is used

        var response = await SignIn(_email, CodeFor(_email));

        AssertSignedIn(response);
        Assert.NotNull(await AccountFor(_email));
    }

    [Fact]
    public async Task A_code_signs_in_an_existing_account_as_itself()
    {
        var existing = await app.AccountFor(_email);
        await RequestCode(_email.ToUpperInvariant());

        AssertSignedIn(await SignIn(_email, CodeFor(_email.ToUpperInvariant())));

        Assert.Equal(existing, await AccountFor(_email));
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
        AssertSignedIn(await SignIn(_email, code));

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

        AssertSignedIn(await SignIn(_email, code));
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
        AssertSignedIn(await SignIn(_email, second));
    }

    [Fact]
    public async Task An_address_gets_at_most_five_codes_an_hour_and_cannot_tell()
    {
        for (var i = 0; i < 5; i++)
            await RequestCode(_email);
        var sixth = await RequestCode(_email);

        Assert.Equal(5, app.Emails.Codes.Count(c => c.Email == _email));
        // The same next step as when a code was sent: nothing tells the two apart.
        Assert.Contains("""autocomplete="one-time-code""", sixth);
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
    }

    [Fact]
    public async Task Requesting_a_code_for_an_implausible_address_stays_on_the_first_step_and_sends_nothing()
    {
        var html = await RequestCode("not-an-email");

        Assert.Contains(SplitIt.Infrastructure.Identity.SignIn.InvalidAddress, html);
        Assert.DoesNotContain(app.Emails.Codes, c => c.Email == "not-an-email");
    }

    // ── helpers: each step from a fresh browser, as a plain form post ─────────────

    private HttpClient Browser() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <returns>The page answered: the code step, or the first step with a reason.</returns>
    private async Task<string> RequestCode(string email)
    {
        var browser = Browser();
        var response = await Forms.Post(browser, "/sign-in/code", await Forms.TokenFrom(browser, "/sign-in"), ("email", email));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> SignIn(string email, string? code)
    {
        var browser = Browser();
        (string, string)[] fields = code is null ? [("email", email)] : [("email", email), ("code", code)];
        return await Forms.Post(browser, "/sign-in", await Forms.TokenFrom(browser, "/sign-in"), fields);
    }

    private string CodeFor(string email) =>
        app.Emails.LatestCodeFor(email) ?? throw new InvalidOperationException($"No code was sent to {email}");

    private async Task<UserId?> AccountFor(string email)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IEmailDirectory>().AccountFor(email);
    }

    private static void AssertSignedIn(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Identity.Application="));
    }

    private static async Task AssertFailed(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(SplitIt.Infrastructure.Identity.SignIn.Failed,
            WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }
}
