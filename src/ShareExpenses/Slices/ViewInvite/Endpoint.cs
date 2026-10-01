using Marten;
using ShareExpenses.Shared;

namespace ShareExpenses.Slices.ViewInvite;

internal sealed record Request(string? Token);

/// <summary>
/// <c>POST /invites/{groupId}/lookup</c> — the invite landing page. No sign-in: the
/// token is the capability (spec §4). POST so the token travels in the body, never in
/// a URL; it changes nothing, so it is safe to repeat.
/// </summary>
internal static class Endpoint
{
    public const string NotFoundReason = "invite not found";

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/invites/{groupId}/lookup", Handle)
            .AllowAnonymous()
            .WithName("ViewInvite");

    // groupId is bound as a string so a malformed one is "not found" like any other
    // dead link, not ASP.NET's 400.
    private static async Task<IResult> Handle(
        string groupId, Request? request, IQuerySession session, TimeProvider clock, CancellationToken ct)
    {
        if (!GroupId.TryParse(groupId, out var group) || string.IsNullOrEmpty(request?.Token))
            return NotFound();

        // Live: fold the one group stream now; nothing is stored (spec §11).
        var state = await session.Events.AggregateStreamAsync<State>(group.Value, token: ct);

        return Reader.Read(state, new Query(group, request.Token, clock.GetUtcNow())) is { } invite
            ? Results.Ok(invite)
            : NotFound();
    }

    private static IResult NotFound() =>
        Results.Problem(NotFoundReason, statusCode: StatusCodes.Status404NotFound);
}
