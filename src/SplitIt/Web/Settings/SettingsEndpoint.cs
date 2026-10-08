using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace SplitIt.Web.Settings;

/// <summary>
/// The Settings screen (spec §3): what belongs to a person and a device, not to a group —
/// the colour theme. Signing out is here too, as an endpoint, but its button is in the home
/// page's menu. Plain forms, no htmx. Not a slice: nothing here is a
/// fact about a group, so there is no event, no command, no state to fold.
///
/// <c>GET /settings</c> is the page; <c>POST /settings/theme</c> sets the cookie and comes
/// back to it; <c>POST /sign-out</c> ends the session, from the home page's menu. Signed in
/// only, like everything else.
/// </summary>
public static class SettingsEndpoint
{
    [WolverineGet("/settings")]
    public static IResult Get(HttpRequest request) => Page(Theme.Of(request) ?? Theme.Device, error: null);

    [ValidateAntiforgery]
    [WolverinePost("/settings/theme")]
    public static async Task<IResult> SetTheme(HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        var posted = await request.ReadFormAsync(ct);
        var choice = posted["theme"].ToString();

        // Only what the page offers. Anything else is a forged post: shown the page, unchanged.
        if (!Theme.IsChoice(choice))
            return Page(Theme.Of(request) ?? Theme.Device, "choose device, light or dark");

        Theme.Choose(response, choice);
        return Results.Redirect("/settings");
    }

    /// <summary>
    /// Ends the session and goes to sign in. The theme stays: it belongs to the device, not
    /// to whoever was signed in on it.
    /// </summary>
    [ValidateAntiforgery]
    [WolverinePost("/sign-out")]
    public static async Task<IResult> SignOut(HttpContext http)
    {
        // The application cookie is the only session there is: no external logins, no two-factor.
        await http.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.Redirect("/sign-in");
    }

    private static IResult Page(string choice, string? error) =>
        new RazorComponentResult<SettingsPage>(new { Choice = choice, Error = error });
}
