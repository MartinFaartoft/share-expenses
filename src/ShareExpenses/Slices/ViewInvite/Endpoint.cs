using Microsoft.AspNetCore.Authorization;
using ShareExpenses.Shared;
using Wolverine.Http;
using Wolverine.Marten;

namespace ShareExpenses.Slices.ViewInvite;

// Public: Wolverine generates code against the endpoint's signature (spec §12).
public sealed record Request(string? Token);

/// <summary>
/// <c>POST /invites/{group}/lookup</c> — the invite landing page. No sign-in: the
/// token is the capability (spec §4). POST so the token travels in the body, never in
/// a URL; it changes nothing, so it is safe to repeat.
///
/// Wolverine folds the group stream live (<c>[ReadAggregate]</c>) and passes it in;
/// nothing is stored (spec §11). What is left here is the read, and its answer.
/// </summary>
public static class Endpoint
{
    public const string NotFoundReason = "invite not found";

    /// <summary>
    /// The stream to read, from the route. A malformed id becomes one no stream has,
    /// so it takes the unknown-group path: the same answer as any dead link.
    /// </summary>
    public static GroupId GroupStream(string group) =>
        GroupId.TryParse(group, out var id) ? id : GroupId.New();

    [WolverinePost("/invites/{group}/lookup", Name = "ViewInvite")]
    [AllowAnonymous]
    public static IResult Post(
        Request? request,
        [ReadAggregate(FromMethod = nameof(GroupStream), Required = false)] State? state,
        TimeProvider clock) =>
        Reader.Read(state, new Query(request?.Token, clock.GetUtcNow())) is { } invite
            ? Results.Ok(invite)
            : Results.Problem(NotFoundReason, statusCode: StatusCodes.Status404NotFound);
}
