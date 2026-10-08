namespace SplitIt.Web;

/// <summary>
/// The colour theme a person has chosen for this device: the <c>theme</c> cookie, rendered as
/// <c>data-theme</c> on <c>&lt;html&gt;</c>. No cookie means the device decides
/// (<c>prefers-color-scheme</c>), which is the default. A property of the browser, not the
/// account, and not an event: a phone at night and a laptop by day rarely want the same.
/// </summary>
public static class Theme
{
    public const string CookieName = "theme";
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>What a person picks on the Settings page: the device's setting, or one of these.</summary>
    public const string Device = "device";

    /// <summary>The chosen theme, or null for the device's own. Anything else in the cookie is ignored: it is rendered into the page.</summary>
    public static string? Of(HttpRequest request) =>
        request.Cookies.TryGetValue(CookieName, out var value) && value is Light or Dark ? value : null;

    public static bool IsChoice(string? value) => value is Device or Light or Dark;

    public static void Choose(HttpResponse response, string choice)
    {
        var options = new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
        };

        if (choice == Device)
            response.Cookies.Delete(CookieName, options);
        else
            response.Cookies.Append(CookieName, choice, new CookieOptions
            {
                Path = options.Path,
                HttpOnly = options.HttpOnly,
                Secure = options.Secure,
                SameSite = options.SameSite,
                IsEssential = options.IsEssential,
                MaxAge = TimeSpan.FromDays(365),
            });
    }
}
