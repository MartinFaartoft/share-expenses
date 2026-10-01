using System.Security.Claims;
using Marten;
using ShareExpenses.Infrastructure.Identity;

namespace ShareExpenses.Slices.CreateGroup;

internal sealed record Request(string? Name, string? Currency, string? DisplayName);

internal sealed record Response(Guid GroupId, Guid MemberId);

/// <summary><c>POST /groups</c> — the New group screen's submit.</summary>
internal static class Endpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/groups", Handle)
            .RequireAuthorization()
            .WithName("CreateGroup");

    private static async Task<IResult> Handle(
        Request request, ClaimsPrincipal user, IDocumentSession session, HttpContext http, CancellationToken ct)
    {
        var command = new Command(request.Name, request.Currency, request.DisplayName, user.UserId());

        var outcome = await Handler.Handle(session, command, Guid.CreateVersion7(), Guid.CreateVersion7(), ct);

        return outcome switch
        {
            Outcome.Created c => Results.Created($"{http.Request.Path}/{c.GroupId}", new Response(c.GroupId, c.MemberId)),
            Outcome.Invalid i => Results.Problem(i.Reason, statusCode: StatusCodes.Status400BadRequest),
            Outcome.AlreadyExists => Results.Problem("group already exists", statusCode: StatusCodes.Status409Conflict),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome}"),
        };
    }
}
