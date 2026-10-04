using System.Security.Claims;
using Marten;
using Marten.Exceptions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;
using Wolverine.Attributes;
using Wolverine.Http;

namespace ShareExpenses.Slices.CreateGroup;

/// <summary>
/// What the New group form shows: the group's id, chosen when the form is shown, and
/// what was typed — empty at first, as submitted after a rejection, with its reason.
/// Public: the screen takes it as a component parameter (spec §3).
/// </summary>
public sealed record NewGroupForm(GroupId GroupId, string GroupName, string Currency, string MemberName, string? Error);

/// <summary>
/// The New group screen: <c>GET /groups/new</c>, the form; <c>POST /groups</c>, its submit.
/// A plain form, no htmx (slice-01-create-group.md).
///
/// No aggregate to fetch: the stream must not exist yet. The submit starts it on the
/// session; <c>[Transactional]</c> has Wolverine save it afterwards.
/// </summary>
public static class Endpoint
{
    /// <summary>Preselected for everyone, until it is guessed from the browser's locale (spec §14).</summary>
    public const string DefaultCurrency = "DKK";

    [WolverineGet("/groups/new")]
    public static IResult Get() =>
        Page(new NewGroupForm(GroupId.New(), "", DefaultCurrency, "", Error: null));

    /// <param name="groupId">
    /// The id the form was shown with, so a resubmitted form names the group it already
    /// created. From the client, so not trusted: a malformed one is replaced.
    /// </param>
    [ValidateAntiforgery]
    [WolverinePost("/groups")]
    [Transactional]
    public static async Task<IResult> Post(
        [FromForm] string? groupId,
        [FromForm] string? groupName,
        [FromForm] string? currency,
        [FromForm] string? memberName,
        ClaimsPrincipal user,
        IDocumentSession session,
        CancellationToken ct)
    {
        var id = GroupId.TryParse(groupId, out var parsed) ? parsed : GroupId.New();

        // Already there: this form was submitted before, and that submit created it
        // (scenario 4). Into it, rather than a second group. Someone else's group goes
        // to a page that answers a non-member with the same 404 as any other.
        if (await session.Events.FetchStreamStateAsync(id.Value, ct) is not null)
            return Results.Redirect($"/groups/{id}");

        // The member id is the server's; only the group's rides in the form.
        var command = new Command(groupName, currency, memberName, user.UserId());
        switch (Decider.Decide(command, id, MemberId.New()))
        {
            case Decision.Accepted accepted:
                // StartStream appends at expected version 0: should a simultaneous submit
                // of the same form have started it meanwhile, the save fails (OnException).
                session.Events.StartStream(id.Value, accepted.Events.ToArray());
                return Results.Redirect($"/groups/{id}");
            case Decision.Rejected rejected:
                // The page again, with what was typed, and why.
                var shown = Currency.TryNormalise(currency, out var code) ? code : currency ?? "";
                return Page(new NewGroupForm(id, groupName ?? "", shown, memberName ?? "", rejected.Reason));
            case var other:
                throw new InvalidOperationException($"Unhandled decision {other}");
        }
    }

    /// <summary>
    /// Two submits of the same form at the same instant: the first saved, this one
    /// found the stream started. A bare 409 for now: a friendly retry is the open
    /// "409 retry UX in forms" task (spec §14).
    /// </summary>
    public static ProblemDetails OnException(ExistingStreamIdCollisionException _) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Detail = "group already exists",
    };

    private static RazorComponentResult<NewGroupPage> Page(NewGroupForm form) => new(new { Form = form });
}
