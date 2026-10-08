using System.Net;
using SplitIt.Shared;
using SplitIt.Tests.Infrastructure;

namespace SplitIt.Tests.Web;

/// <summary>
/// The Settings screen: the colour theme, kept in a cookie on this device and rendered on
/// every page, and signing out. Not a slice (no events), so its tests are not either.
/// </summary>
[Collection(AppCollection.Name)]
public class SettingsTests(AppFixture app)
{
    private readonly UserId _alice = UserId.New();

    // ── The page ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Signed_out_it_redirects_to_sign_in_and_back()
    {
        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/settings");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in?returnUrl=%2Fsettings", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task By_default_the_device_decides_and_the_page_offers_the_three_choices()
    {
        var html = await Page(app.BrowserFor(_alice));

        Assert.Contains("<title>Settings · SplitIt</title>", html);
        Assert.Contains("""<a class="up" href="/" aria-label="Back">‹</a>""", html);
        Assert.Contains("""<input type="radio" name="theme" value="device" checked />""", html);
        Assert.Contains("""<input type="radio" name="theme" value="light" />""", html);
        Assert.Contains("""<input type="radio" name="theme" value="dark" />""", html);
        Assert.Contains("""<form method="post" action="/settings/theme">""", html);
        Assert.DoesNotContain("sign-out", html);
        Assert.NotNull(Forms.TokenIn(html));
    }

    [Fact]
    public async Task The_home_pages_menu_offers_settings_and_then_sign_out_as_a_post()
    {
        var html = WebUtility.HtmlDecode(await app.BrowserFor(_alice).GetStringAsync("/"));

        Assert.Contains("""<a href="/settings">Settings</a>""", html);
        Assert.Matches(@"<form method=""post"" action=""/sign-out"">\s*<input type=""hidden"" name=""__RequestVerificationToken""[^>]*>\s*<button type=""submit"">Sign out</button>\s*</form>", html);
        Assert.True(html.IndexOf("Settings</a>", StringComparison.Ordinal) < html.IndexOf("Sign out</button>", StringComparison.Ordinal));
        Assert.NotNull(Forms.TokenIn(html));
    }

    [Fact]
    public async Task Only_the_home_page_has_sign_out()
    {
        var browser = app.BrowserFor(_alice);

        Assert.DoesNotContain("/sign-out", await browser.GetStringAsync("/settings"));
        Assert.DoesNotContain("/sign-out", await browser.GetStringAsync("/groups/new"));
    }

    // ── The choice ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public async Task Choosing_a_theme_sets_a_cookie_for_this_device_and_comes_back_to_the_page(string choice)
    {
        var browser = app.BrowserFor(_alice);

        var response = await Choose(browser, choice);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/settings", response.Headers.Location?.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("theme="));
        Assert.StartsWith($"theme={choice};", cookie);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=31536000", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_chosen_theme_is_what_the_page_shows_selected_next()
    {
        var browser = app.BrowserFor(_alice);
        browser.DefaultRequestHeaders.Add("Cookie", "theme=dark");

        var html = await Page(browser);

        Assert.Contains("""<input type="radio" name="theme" value="dark" checked />""", html);
        Assert.Contains("""<input type="radio" name="theme" value="device" />""", html);
    }

    [Fact]
    public async Task Choosing_the_device_again_removes_the_cookie()
    {
        var browser = app.BrowserFor(_alice);

        var response = await Choose(browser, "device");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("theme="));
        Assert.StartsWith("theme=;", cookie);
        Assert.Contains("expires=Thu, 01 Jan 1970", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("neon")]
    [InlineData("")]
    [InlineData("Dark")]
    [InlineData("\"><script>alert(1)</script>")]
    public async Task Anything_the_page_did_not_offer_is_refused_and_sets_nothing(string forged)
    {
        var browser = app.BrowserFor(_alice);

        var response = await Choose(browser, forged);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("""<p class="error" role="alert">choose device, light or dark</p>""", html);
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [], c => c.StartsWith("theme="));
    }

    [Fact]
    public async Task A_missing_choice_is_refused_not_a_server_error()
    {
        var browser = app.BrowserFor(_alice);
        var token = Forms.TokenIn(await Page(browser))!;

        var response = await Forms.Post(browser, "/settings/theme", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("choose device, light or dark", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_a_valid_antiforgery_token_the_choice_is_400()
    {
        var browser = app.BrowserFor(_alice);
        await Page(browser);

        var response = await Forms.Post(browser, "/settings/theme", "forged", ("theme", "dark"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── The theme, on every page ─────────────────────────────────────────────────

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public async Task Every_page_carries_the_chosen_theme_signed_in_or_not(string choice)
    {
        var signedIn = app.BrowserFor(_alice);
        signedIn.DefaultRequestHeaders.Add("Cookie", $"theme={choice}");
        var signedOut = app.CreateClient();
        signedOut.DefaultRequestHeaders.Add("Cookie", $"theme={choice}");

        foreach (var html in new[]
        {
            await signedIn.GetStringAsync("/"),
            await signedIn.GetStringAsync("/settings"),
            await signedIn.GetStringAsync("/groups/new"),
            await signedOut.GetStringAsync("/sign-in"),
        })
            Assert.Contains($"""<html lang="en" data-theme="{choice}">""", html);
    }

    [Fact]
    public async Task With_no_choice_the_page_says_nothing_and_the_device_decides()
    {
        var html = await app.BrowserFor(_alice).GetStringAsync("/");

        Assert.Contains("""<html lang="en">""", html);
        Assert.DoesNotContain("data-theme", html);
    }

    [Theory]
    [InlineData("neon")]
    [InlineData("DARK")]
    [InlineData("dark\"><script>")]
    public async Task A_cookie_with_anything_else_is_ignored_and_never_reaches_the_page(string value)
    {
        var browser = app.BrowserFor(_alice);
        browser.DefaultRequestHeaders.Add("Cookie", $"theme={Uri.EscapeDataString(value)}");

        var html = await browser.GetStringAsync("/");

        Assert.DoesNotContain("data-theme", html);
        Assert.DoesNotContain("neon", html);
        Assert.DoesNotContain("<script>", html.Replace("<script src=", ""));
    }

    [Fact]
    public async Task The_stylesheet_follows_the_device_unless_a_theme_is_chosen()
    {
        var css = await app.CreateClient().GetStringAsync("/css/site.css");

        Assert.Contains("@media (prefers-color-scheme: dark)", css);
        Assert.Contains(":root:not([data-theme=\"light\"])", css);
        Assert.Contains(":root[data-theme=\"dark\"]", css);
        Assert.Contains("color-scheme: light dark;", css);
    }

    [Fact]
    public async Task No_colour_is_written_into_the_stylesheet_outside_its_two_palettes()
    {
        var css = await app.CreateClient().GetStringAsync("/css/site.css");
        var rest = css[css.IndexOf("* { box-sizing", StringComparison.Ordinal)..];

        Assert.DoesNotMatch("#[0-9a-fA-F]{3,6}\\b", rest);
    }

    // ── Signing out ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Signing_out_ends_the_session_and_goes_to_sign_in()
    {
        var browser = app.BrowserFor(_alice);
        var token = await Forms.TokenFrom(browser, "/");

        var response = await Forms.Post(browser, "/sign-out", token);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in", response.Headers.Location?.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(".AspNetCore.Identity.Application=;") && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Signing_out_keeps_the_theme_it_belongs_to_the_device()
    {
        var browser = app.BrowserFor(_alice);
        var token = await Forms.TokenFrom(browser, "/");

        var response = await Forms.Post(browser, "/sign-out", token);

        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("theme="));
    }

    [Fact]
    public async Task Without_a_valid_antiforgery_token_signing_out_is_400_and_ends_nothing()
    {
        var browser = app.BrowserFor(_alice);
        await browser.GetAsync("/");

        var response = await Forms.Post(browser, "/sign-out", "forged");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith(".AspNetCore.Identity.Application=")));
    }

    [Fact]
    public async Task A_link_cannot_sign_anyone_out_it_has_to_be_a_post()
    {
        var response = await app.BrowserFor(_alice).GetAsync("/sign-out");

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Signed_out_there_is_nothing_to_sign_out_of()
    {
        var token = await Forms.TokenFrom(app.CreateClient(new() { AllowAutoRedirect = false }), "/sign-in");

        var response = await Forms.Post(app.CreateClient(new() { AllowAutoRedirect = false }), "/sign-out", token);

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.BadRequest);
        Assert.StartsWith("/sign-in", response.Headers.Location?.OriginalString ?? "/sign-in");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static async Task<string> Page(HttpClient browser)
    {
        var response = await browser.GetAsync("/settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> Choose(HttpClient browser, string choice) =>
        await Forms.Post(browser, "/settings/theme", Forms.TokenIn(await Page(browser))!, ("theme", choice));
}
