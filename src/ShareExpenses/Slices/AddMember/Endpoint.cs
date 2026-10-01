using System.Security.Claims;
using Marten;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;

namespace ShareExpenses.Slices.AddMember;

internal sealed record Request(string? DisplayName);

internal sealed record Response(MemberId MemberId);

/// <summary><c>POST /groups/{groupId}/members</c> — the Group setup screen's "add".</summary>
internal static class Endpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/groups/{groupId}/members", Handle)
            .RequireAuthorization()
            .WithName("AddMember");

    // groupId is bound as a string, not a GroupId, so a malformed id is answered
    // by us — 404, like a missing group — rather than by ASP.NET's binder (400).
    private static async Task<IResult> Handle(
        string groupId, Request request, ClaimsPrincipal user, IDocumentSession session, HttpContext http,
        CancellationToken ct)
    {
        if (!GroupId.TryParse(groupId, out var id))
            return NotFound(Decider.GroupNotFound);

        var outcome = await Handler.Handle(session, new Command(id, request.DisplayName, user.UserId()), MemberId.New(), ct);

        return outcome switch
        {
            Outcome.Added a => Results.Created($"{http.Request.Path}/{a.MemberId}", new Response(a.MemberId)),
            Outcome.Invalid i => Results.Problem(i.Reason, statusCode: StatusCodes.Status400BadRequest),
            Outcome.NotFound n => NotFound(n.Reason),
            Outcome.Conflict => Results.Problem(
                "the group changed while you were adding; please try again", statusCode: StatusCodes.Status409Conflict),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome}"),
        };
    }

    private static IResult NotFound(string reason) =>
        Results.Problem(reason, statusCode: StatusCodes.Status404NotFound);
}
