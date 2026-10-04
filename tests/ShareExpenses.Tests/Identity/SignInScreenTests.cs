using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Tests.Infrastructure;

namespace ShareExpenses.Tests.Identity;

/// <summary>
/// The sign-in screen (spec §3, §4): HTML over the same flow as the JSON API. Each
/// test is a browser: it loads the page, keeps its cookies, and posts the form with
/// the antiforgery token the page carries — as htmx does, or as a plain form.
/// </summary>
[Collection(AppCollection.Name)]
public partial class SignInScreenTests(AppFixture app)
{
    private readonly string _email = $"someone-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task The_screen_asks_for_an_address_and_carries_layout_htmx_and_a_token()
    {
        var html = await (await Browser().GetAsync("/sign-in")).Content.ReadAsStringAsync();

        Assert.Contains("<title>Sign in · Shared expenses</title>", html);
        Assert.Contains("""href="/css/site.css""", html);
        Assert.Contains("""src="/js/htmx-2.0.4.min.js""", html);
        Assert.Contains("""name="email" type="email""", html);
        Assert.Contains("""autocomplete="email""", html);
        Assert.Matches(TokenPattern(), html);
    }

    [Fact]
    public async Task With_htmx_each_step_swaps_in_place_and_success_redirects_home()
    {
        var browser = Browser();
        var token = await TokenFrom(browser, "/sign-in");

        var step2 = await Post(browser, "/sign-in/code", htmx: true, ("email", _email), ("__RequestVerificationToken", token));
        var fragment = await step2.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, step2.StatusCode);
        Assert.DoesNotContain("<html", fragment);
        Assert.Contains("""autocomplete="one-time-code""", fragment);
        Assert.Contains("""inputmode="numeric""", fragment);
        Assert.Contains("""maxlength="6""", fragment);
        Assert.Contains(_email, fragment);

        var signedIn = await Post(browser, "/sign-in", htmx: true,
            ("email", _email), ("code", CodeFor(_email)), ("__RequestVerificationToken", TokenIn(fragment)));

        Assert.Equal(HttpStatusCode.NoContent, signedIn.StatusCode);
        Assert.Equal("/", signedIn.Headers.GetValues("HX-Redirect").Single());
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Identity.Application="));
    }

    [Fact]
    public async Task Without_htmx_plain_form_posts_work_too()
    {
        var browser = Browser();

        var step2 = await Post(browser, "/sign-in/code", htmx: false,
            ("email", _email), ("__RequestVerificationToken", await TokenFrom(browser, "/sign-in")));
        var page = await step2.Content.ReadAsStringAsync();
        Assert.Contains("<html", page);
        Assert.Contains("""autocomplete="one-time-code""", page);

        var signedIn = await Post(browser, "/sign-in", htmx: false,
            ("email", _email), ("code", CodeFor(_email)), ("__RequestVerificationToken", TokenIn(page)));

        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/", signedIn.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task A_wrong_code_is_answered_in_place_with_the_one_failure_message()
    {
        var browser = Browser();
        var fragment = await (await Post(browser, "/sign-in/code", htmx: true,
            ("email", _email), ("__RequestVerificationToken", await TokenFrom(browser, "/sign-in")))).Content.ReadAsStringAsync();
        var wrong = CodeFor(_email) == "000000" ? "000001" : "000000";

        var response = await Post(browser, "/sign-in", htmx: true,
            ("email", _email), ("code", wrong), ("__RequestVerificationToken", TokenIn(fragment)));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(SignIn.Failed, html);
        Assert.Contains("""autocomplete="one-time-code""", html);
    }

    [Fact]
    public async Task An_implausible_address_stays_on_the_first_step_with_the_reason()
    {
        var browser = Browser();

        var response = await Post(browser, "/sign-in/code", htmx: true,
            ("email", "not-an-email"), ("__RequestVerificationToken", await TokenFrom(browser, "/sign-in")));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains(SignIn.InvalidAddress, html);
        Assert.Contains("""name="email" type="email" value="not-an-email""", html);
    }

    [Fact]
    public async Task A_post_without_a_valid_antiforgery_token_is_400_and_sends_nothing()
    {
        var browser = Browser();
        await browser.GetAsync("/sign-in");     // the antiforgery cookie, but no token posted

        var missing = await Post(browser, "/sign-in/code", htmx: true, ("email", _email));
        var forged = await Post(browser, "/sign-in/code", htmx: true, ("email", _email), ("__RequestVerificationToken", "forged"));

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.DoesNotContain(app.Emails.Codes, c => c.Email == _email);
    }

    [Fact]
    public async Task Home_asked_for_while_signed_out_redirects_to_sign_in()
    {
        var response = await Browser().GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Any_other_screen_asked_for_while_signed_out_is_returned_to_after_signing_in()
    {
        var browser = Browser();

        var redirect = await browser.GetAsync("/groups/some-group?tab=history");
        Assert.Equal("/sign-in?returnUrl=%2Fgroups%2Fsome-group%3Ftab%3Dhistory", redirect.Headers.Location?.OriginalString);

        var page = await (await browser.GetAsync(redirect.Headers.Location!.OriginalString)).Content.ReadAsStringAsync();
        Assert.Contains("""name="returnUrl" value="/groups/some-group?tab=history""", WebUtility.HtmlDecode(page));

        var step2 = await (await Post(browser, "/sign-in/code", htmx: true,
            ("email", _email), ("returnUrl", "/groups/some-group?tab=history"), ("__RequestVerificationToken", TokenIn(page))))
            .Content.ReadAsStringAsync();
        Assert.Contains("""name="returnUrl" value="/groups/some-group?tab=history""", WebUtility.HtmlDecode(step2));

        var signedIn = await Post(browser, "/sign-in", htmx: true, ("email", _email), ("code", CodeFor(_email)),
            ("returnUrl", "/groups/some-group?tab=history"), ("__RequestVerificationToken", TokenIn(step2)));
        Assert.Equal("/groups/some-group?tab=history", signedIn.Headers.GetValues("HX-Redirect").Single());
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("groups/x")]
    public async Task A_return_url_off_this_site_is_ignored_and_goes_home(string returnUrl)
    {
        var browser = Browser();
        var step2 = await (await Post(browser, "/sign-in/code", htmx: true,
            ("email", _email), ("__RequestVerificationToken", await TokenFrom(browser, "/sign-in")))).Content.ReadAsStringAsync();

        var signedIn = await Post(browser, "/sign-in", htmx: false, ("email", _email), ("code", CodeFor(_email)),
            ("returnUrl", returnUrl), ("__RequestVerificationToken", TokenIn(step2)));

        Assert.Equal("/", signedIn.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Signed_in_home_is_shown()
    {
        var response = await app.ClientFor(ShareExpenses.Shared.UserId.New()).GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Your groups", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/css/site.css")]
    [InlineData("/js/htmx-2.0.4.min.js")]
    public async Task The_stylesheet_and_htmx_are_served_to_anyone(string path) =>
        Assert.Equal(HttpStatusCode.OK, (await Browser().GetAsync(path)).StatusCode);

    // ── a browser ─────────────────────────────────────────────────────────────────

    /// <summary>Keeps cookies, as a browser does; does not follow redirects, so tests see them.</summary>
    private HttpClient Browser() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static Task<HttpResponseMessage> Post(
        HttpClient browser, string path, bool htmx, params (string Name, string Value)[] form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(form.Select(f => KeyValuePair.Create(f.Name, f.Value))),
        };
        if (htmx)
            request.Headers.Add("HX-Request", "true");
        return browser.SendAsync(request);
    }

    private static async Task<string> TokenFrom(HttpClient browser, string path) =>
        TokenIn(await (await browser.GetAsync(path)).Content.ReadAsStringAsync());

    private static string TokenIn(string html) =>
        TokenPattern().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value)
            : throw new InvalidOperationException("No antiforgery token in the page");

    private string CodeFor(string email) =>
        app.Emails.LatestCodeFor(email) ?? throw new InvalidOperationException($"No code was sent to {email}");

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
