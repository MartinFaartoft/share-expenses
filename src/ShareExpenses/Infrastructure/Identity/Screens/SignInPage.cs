using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ShareExpenses.Web;

namespace ShareExpenses.Infrastructure.Identity.Screens;

/// <summary>
/// The sign-in screen's endpoints (spec §3, §4): the same <see cref="SignInFlow"/> as
/// the JSON API, as HTML. Works as plain form posts; with htmx, each step swaps in
/// place and success redirects. Form posts carry an antiforgery token.
/// </summary>
internal static class SignInPage
{
    public static void Map(RouteGroupBuilder signIn)
    {
        signIn.MapGet("", () => Screen(email: "", error: null, askForCode: false)).WithName("SignInScreen");
        signIn.MapPost("/code", RequestCode).WithName("SignInScreenRequestCode");
        signIn.MapPost("", VerifyCode).WithName("SignInScreenVerify");
    }

    private static async Task<IResult> RequestCode(
        [FromForm] string? email, HttpRequest request, SignInFlow flow, CancellationToken ct)
    {
        var address = email?.Trim() ?? "";
        return await flow.RequestCode(address, ct)
            ? Step<CodeStep>(request, address, error: null, askForCode: true)
            : Step<EmailStep>(request, address, SignIn.InvalidAddress, askForCode: false);
    }

    private static async Task<IResult> VerifyCode(
        [FromForm] string? email, [FromForm] string? code, HttpRequest request, HttpResponse response,
        SignInFlow flow, CancellationToken ct)
    {
        var address = email?.Trim() ?? "";
        if (await flow.Verify(address, code, ct) is null)
            return Step<CodeStep>(request, address, SignIn.Failed, askForCode: true);

        // Signed in: htmx follows HX-Redirect; a plain form post follows a 303.
        if (!request.IsHtmx())
            return Results.Redirect("/", permanent: false, preserveMethod: false);
        response.Headers["HX-Redirect"] = "/";
        return Results.NoContent();
    }

    /// <summary>A step: just the fragment for htmx, the whole screen otherwise.</summary>
    private static IResult Step<TStep>(HttpRequest request, string email, string? error, bool askForCode)
        where TStep : Microsoft.AspNetCore.Components.IComponent =>
        request.IsHtmx()
            ? new RazorComponentResult<TStep>(new { Email = email, Error = error })
            : Screen(email, error, askForCode);

    private static RazorComponentResult<SignInScreen> Screen(string email, string? error, bool askForCode) =>
        new(new { Email = email, Error = error, AskForCode = askForCode });
}
