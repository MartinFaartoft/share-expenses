using System.Security.Claims;
using Marten;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;

namespace ShareExpenses.Slices.ClaimMember;

internal sealed record Request(string? Token);

internal sealed record Response(GroupId GroupId, MemberId MemberId);

/// <summary>
/// <c>POST /invites/{groupId}/claim</c> — "Sign me in" on the invite landing page,
/// after signing in. The token travels in the body, never in a URL (spec §11).
/// </summary>
internal static class Endpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/invites/{groupId}/claim", Handle)
            .RequireAuthorization()
            .WithName("ClaimMember");

    // groupId is bound as a string so a malformed one is "invite not found" like any
    // other dead link, not ASP.NET's 400.
    private static async Task<IResult> Handle(
        string groupId, Request? request, ClaimsPrincipal user, IDocumentSession session, TimeProvider clock,
        CancellationToken ct)
    {
        if (!GroupId.TryParse(groupId, out var group))
            return NotFound(Decider.InviteNotFound);

        var command = new Command(group, request?.Token, clock.GetUtcNow(), user.UserId());

        return await Handler.Handle(session, command, ct) switch
        {
            Outcome.Claimed c => Results.Ok(new Response(group, c.MemberId)),
            Outcome.NotFound n => NotFound(n.Reason),
            Outcome.AlreadyMember a => Results.Problem(
                a.Reason, statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["groupId"] = group, ["memberId"] = a.MemberId }),
            Outcome.Conflict => Results.Problem(
                "the group changed while you were joining; please try again",
                statusCode: StatusCodes.Status409Conflict),
            var other => throw new InvalidOperationException($"Unhandled outcome {other}"),
        };
    }

    private static IResult NotFound(string reason) =>
        Results.Problem(reason, statusCode: StatusCodes.Status404NotFound);
}
