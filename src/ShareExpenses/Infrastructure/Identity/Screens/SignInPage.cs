using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ShareExpenses.Web;

namespace ShareExpenses.Infrastructure.Identity.Screens;

/// <summary>
/// The sign-in screen's endpoints (spec §3, §4), over <see cref="SignInFlow"/>. Works as plain form posts; with htmx, each step swaps in
/// place and success redirects. Form posts carry an antiforgery token. A
/// <c>returnUrl</c> — where the user was going — rides along, and is followed on
/// success only if it is a path on this site.
/// </summary>
internal static class SignInPage
{
    public static void Map(RouteGroupBuilder signIn)
    {
        signIn.MapGet("", ([FromQuery] string? returnUrl) => Screen(email: "", error: null, askForCode: false, returnUrl))
            .WithName("SignInScreen");
        signIn.MapPost("/code", RequestCode).WithName("SignInScreenRequestCode");
        signIn.MapPost("", VerifyCode).WithName("SignInScreenVerify");
    }

    private static async Task<IResult> RequestCode(
        [FromForm] string? email, [FromForm] string? returnUrl, HttpRequest request, SignInFlow flow, CancellationToken ct)
    {
        var address = email?.Trim() ?? "";
        return await flow.RequestCode(address, ct)
            ? Step<CodeStep>(request, address, error: null, askForCode: true, returnUrl)
            : Step<EmailStep>(request, address, SignIn.InvalidAddress, askForCode: false, returnUrl);
    }

    private static async Task<IResult> VerifyCode(
        [FromForm] string? email, [FromForm] string? code, [FromForm] string? returnUrl,
        HttpRequest request, HttpResponse response, SignInFlow flow, CancellationToken ct)
    {
        var address = email?.Trim() ?? "";
        if (await flow.Verify(address, code, ct) is null)
            return Step<CodeStep>(request, address, SignIn.Failed, askForCode: true, returnUrl);

        // Signed in: where they were going, if on this site, or home. htmx follows
        // HX-Redirect; a plain form post follows the redirect.
        var next = WebSetup.LocalOrHome(returnUrl);
        if (!request.IsHtmx())
            return Results.Redirect(next);
        response.Headers["HX-Redirect"] = next;
        return Results.NoContent();
    }

    /// <summary>A step: just the fragment for htmx, the whole screen otherwise.</summary>
    private static IResult Step<TStep>(HttpRequest request, string email, string? error, bool askForCode, string? returnUrl)
        where TStep : Microsoft.AspNetCore.Components.IComponent =>
        request.IsHtmx()
            ? new RazorComponentResult<TStep>(new { Email = email, Error = error, ReturnUrl = returnUrl })
            : Screen(email, error, askForCode, returnUrl);

    private static RazorComponentResult<SignInScreen> Screen(string email, string? error, bool askForCode, string? returnUrl) =>
        new(new { Email = email, Error = error, AskForCode = askForCode, ReturnUrl = returnUrl });
}
