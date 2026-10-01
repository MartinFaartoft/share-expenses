using Microsoft.AspNetCore.Identity;

namespace ShareExpenses.Infrastructure.Identity;

/// <summary>
/// <c>POST /dev/sign-in</c>: signs in as any email address, creating the user if
/// needed — a stand-in until the magic-link flow (spec §4) exists, so the API can
/// be exercised by hand. Mapped only in Development; in any other environment the
/// route does not exist at all.
/// </summary>
public static class DevSignIn
{
    private sealed record Request(string Email);

    public static void MapDevSignIn(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;

        app.MapPost("/dev/sign-in", async (Request request, UserManager<User> users, SignInManager<User> signIn) =>
        {
            var user = await users.FindByEmailAsync(request.Email);
            if (user is null)
            {
                user = new User { UserName = request.Email, Email = request.Email, EmailConfirmed = true };
                var created = await users.CreateAsync(user);
                if (!created.Succeeded)
                    return Results.Problem(string.Join("; ", created.Errors.Select(e => e.Description)), statusCode: 400);
            }

            await signIn.SignInAsync(user, isPersistent: true);
            return Results.Ok(new { userId = user.Id, user.Email });
        });
    }
}
