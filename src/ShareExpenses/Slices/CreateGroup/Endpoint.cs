using System.Security.Claims;
using Marten;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;

namespace ShareExpenses.Slices.CreateGroup;

internal sealed record Request(string? GroupName, string? Currency, string? MemberName);

internal sealed record Response(GroupId GroupId, MemberId MemberId);

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
        var command = new Command(request.GroupName, request.Currency, request.MemberName, user.UserId());

        var outcome = await Handler.Handle(session, command, GroupId.New(), MemberId.New(), ct);

        return outcome switch
        {
            Outcome.Created c => Results.Created($"{http.Request.Path}/{c.GroupId}", new Response(c.GroupId, c.MemberId)),
            Outcome.Invalid i => Results.Problem(i.Reason, statusCode: StatusCodes.Status400BadRequest),
            Outcome.AlreadyExists => Results.Problem("group already exists", statusCode: StatusCodes.Status409Conflict),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome}"),
        };
    }
}
