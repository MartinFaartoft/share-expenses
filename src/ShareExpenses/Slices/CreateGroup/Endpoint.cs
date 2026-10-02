using System.Security.Claims;
using Marten;
using Marten.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;
using Wolverine.Attributes;
using Wolverine.Http;

namespace ShareExpenses.Slices.CreateGroup;

// Public: Wolverine generates code against the endpoint's signature (spec §12).
public sealed record Request(string? GroupName, string? Currency, string? MemberName);

internal sealed record Response(GroupId GroupId, MemberId MemberId);

/// <summary>
/// <c>POST /groups</c> — the New group screen's submit.
///
/// No aggregate to fetch: the stream must not exist yet. The endpoint starts it on
/// the session; <c>[Transactional]</c> has Wolverine save it afterwards.
/// </summary>
public static class Endpoint
{
    [WolverinePost("/groups", Name = "CreateGroup")]
    [Authorize]
    [Transactional]
    public static IResult Post(Request request, ClaimsPrincipal user, IDocumentSession session, HttpContext http)
    {
        // Ids are chosen here, so deciding stays deterministic (spec §12).
        var groupId = GroupId.New();
        var memberId = MemberId.New();
        var command = new Command(request.GroupName, request.Currency, request.MemberName, user.UserId());

        switch (Decider.Decide(command, groupId, memberId))
        {
            case Decision.Accepted accepted:
                // StartStream appends at expected version 0: if the stream already exists
                // the save fails, which is what guarantees a group is created exactly once.
                session.Events.StartStream(groupId.Value, accepted.Events.ToArray());
                return Results.Created($"{http.Request.Path}/{groupId}", new Response(groupId, memberId));
            case Decision.Rejected rejected:
                return Results.Problem(rejected.Reason, statusCode: StatusCodes.Status400BadRequest);
            case var other:
                throw new InvalidOperationException($"Unhandled decision {other}");
        }
    }

    /// <summary>Wolverine's save found the stream already started (scenario 4).</summary>
    public static ProblemDetails OnException(ExistingStreamIdCollisionException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "group already exists",
    };
}
