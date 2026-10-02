using System.Security.Claims;
using JasperFx;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;
using Wolverine.Http;
using Wolverine.Marten;

namespace ShareExpenses.Slices.AddMember;

// Public: Wolverine generates code against the endpoint's signature (spec §12).
public sealed record Request(string? DisplayName);

internal sealed record Response(MemberId MemberId);

/// <summary>
/// <c>POST /groups/{group}/members</c> — the Group setup screen's "add".
///
/// Wolverine's aggregate handler workflow: before this method runs, Wolverine has
/// called <c>FetchForWriting&lt;State&gt;</c> for the route's group id; afterwards it
/// appends the returned events at the version it read and saves. What is left here
/// is decide, and map the decision to HTTP.
/// </summary>
public static class Endpoint
{
    /// <summary>
    /// The stream to fetch, from the route. A malformed id becomes one no stream has,
    /// so it takes the unknown-group path: same answer, same work.
    /// </summary>
    public static GroupId GroupStream(string group) =>
        GroupId.TryParse(group, out var id) ? id : GroupId.New();

    [WolverinePost("/groups/{group}/members", Name = "AddMember")]
    public static (IResult, Events) Post(
        Request request,
        // Required = false: a missing stream arrives as null and is answered by
        // Decide, so "no such group" and "not a member" give the same 404 body.
        [WriteAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        ClaimsPrincipal user,
        HttpContext http)
    {
        var memberId = MemberId.New();
        var command = new Command(request.DisplayName, user.UserId());

        return Decider.Decide(state, command, memberId) switch
        {
            Decision.Accepted accepted => (
                Results.Created($"{http.Request.Path}/{memberId}", new Response(memberId)),
                [.. accepted.Events]),
            Decision.Rejected { Kind: Rejection.NotFound } rejected => (
                Results.Problem(rejected.Reason, statusCode: StatusCodes.Status404NotFound),
                []),
            Decision.Rejected rejected => (
                Results.Problem(rejected.Reason, statusCode: StatusCodes.Status400BadRequest),
                []),
            var other => throw new InvalidOperationException($"Unhandled decision {other}"),
        };
    }

    /// <summary>Wolverine's save lost the race: another phone appended first.</summary>
    // DECISION (AddMember): on a concurrency conflict the CLIENT retries — we return
    // 409 and do nothing else. This is spec §11 as written ("the loser retries"),
    // and keeps the conflict visible while learning.
    //
    // Revisit: a server-side retry (re-fetch, re-decide, re-append, a few times)
    // would be correct, not a blind overwrite — the rules run again against the new
    // state, so two phones adding "Bob" still yields one "already exists". It would
    // turn most 409s into a single round trip.
    public static ProblemDetails OnException(ConcurrencyException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "the group changed while you were adding; please try again",
    };
}
