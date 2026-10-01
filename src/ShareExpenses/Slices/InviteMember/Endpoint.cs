using System.Security.Claims;
using Marten;
using ShareExpenses.Infrastructure;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;

namespace ShareExpenses.Slices.InviteMember;

internal sealed record Request(string? Email);

internal sealed record Response(string Link);

/// <summary><c>POST /groups/{groupId}/members/{memberId}/invite</c> — the Invite member screen's submit.</summary>
internal static class Endpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/groups/{groupId}/members/{memberId}/invite", Handle)
            .RequireAuthorization()
            .WithName("InviteMember");

    // Ids are bound as strings so malformed ones are answered by us, not ASP.NET's
    // binder (400). A malformed groupId is 404 like a missing group. A malformed
    // memberId still goes through deciding — as an id no slot has — so membership
    // is checked first and a non-member still only ever sees "group not found".
    private static async Task<IResult> Handle(
        string groupId, string memberId, Request request, ClaimsPrincipal user,
        IDocumentSession session, IEmailDirectory directory, IEmailSender email, PublicOrigin origin,
        TimeProvider clock, ILoggerFactory loggers, HttpContext http, CancellationToken ct)
    {
        if (!GroupId.TryParse(groupId, out var group))
            return NotFound(Decider.GroupNotFound);
        var member = MemberId.TryParse(memberId, out var parsed) ? parsed : MemberId.From(Guid.Empty);

        var (token, tokenHash) = InviteToken.Generate();
        var command = new Command(group, member, request.Email, tokenHash, clock.GetUtcNow(), user.UserId());

        switch (await Handler.Handle(session, directory, command, ct))
        {
            case Outcome.Invited invited:
                // The token goes in the fragment: browsers never send it to a server,
                // so it stays out of proxy and server logs. Not out of browser history or
                // bookmarks — a fragment is stored and synced like any other part of a URL.
                var link = $"{origin.For(http.Request)}/invites/{group}#{token}";
                await SendEmail(email, loggers, command, link, invited, ct);
                return Results.Ok(new Response(link));
            case Outcome.Invalid i:
                return Results.Problem(i.Reason, statusCode: StatusCodes.Status400BadRequest);
            case Outcome.NotFound n:
                return NotFound(n.Reason);
            case Outcome.Conflict:
                return Results.Problem(
                    "the group changed while you were inviting; please try again",
                    statusCode: StatusCodes.Status409Conflict);
            case var other:
                throw new InvalidOperationException($"Unhandled outcome {other}");
        }
    }

    /// <summary>
    /// After the save: the invite has happened whether or not the email arrives, and
    /// the link is in the response either way. A failure is logged, not returned.
    /// </summary>
    private static async Task SendEmail(
        IEmailSender email, ILoggerFactory loggers, Command command, string link, Outcome.Invited invited,
        CancellationToken ct)
    {
        try
        {
            await email.SendInviteAsync(
                invited.Email, link, invited.GroupName, invited.InviterName, invited.MemberName, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            loggers.CreateLogger(typeof(Endpoint)).LogError(e,
                "Invite to group {GroupId} for member {MemberId} was recorded, but its email could not be sent",
                command.GroupId, command.MemberId);
        }
    }

    private static IResult NotFound(string reason) =>
        Results.Problem(reason, statusCode: StatusCodes.Status404NotFound);
}
