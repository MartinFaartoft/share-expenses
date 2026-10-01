using JasperFx;
using Marten;
using ShareExpenses.Infrastructure.Identity;
using ShareExpenses.Shared;

namespace ShareExpenses.Slices.InviteMember;

internal abstract record Outcome
{
    private Outcome() { }

    /// <summary>Saved. Carries what the invite email needs to say.</summary>
    public sealed record Invited(string Email, string GroupName, string InviterName, string MemberName) : Outcome;

    public sealed record Invalid(string Reason) : Outcome;

    public sealed record GroupNotFound : Outcome;

    public sealed record MemberNotFound : Outcome;

    public sealed record Conflict : Outcome;
}

/// <summary>
/// Look up what the guards need, fetch the group's state, decide, then append the
/// event and store the delivery document — one session, one transaction.
/// </summary>
internal static class Handler
{
    public static async Task<Outcome> Handle(
        IDocumentSession session, IEmailDirectory directory, Command command, CancellationToken ct)
    {
        var email = Decider.NormaliseEmail(command.Email);
        command = command with
        {
            EmailHolder = email.Length > 0 ? await directory.AccountFor(email, ct) : null,
            InvitedTo = await SlotsInvitedAt(session, command.GroupId, email, ct),
        };

        var stream = await session.Events.FetchForWriting<State>(command.GroupId.Value, ct);
        var state = stream.Aggregate;

        switch (Decider.Decide(state, command))
        {
            case Decision.Rejected { Reason: Decider.GroupNotFound }:
                return new Outcome.GroupNotFound();
            case Decision.Rejected { Reason: Decider.MemberNotFound }:
                return new Outcome.MemberNotFound();
            case Decision.Rejected rejected:
                return new Outcome.Invalid(rejected.Reason);
            case Decision.Accepted accepted:
                stream.AppendMany(accepted.Events);
                session.Store(new InviteDelivery
                {
                    Id = command.MemberId.Value,
                    GroupId = command.GroupId,
                    TokenHash = command.TokenHash,
                    Email = email,
                });
                break;
        }

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (ConcurrencyException)
        {
            // Client retries on 409, as in AddMember — see the decision recorded in
            // Slices/AddMember/Handler.cs and spec §11.
            return new Outcome.Conflict();
        }

        // Accepted implies the actor is a member and the slot exists.
        var inviter = state!.Slots[state.Members[command.By]].Name;
        return new Outcome.Invited(email, state.GroupName, inviter, state.Slots[command.MemberId].Name);
    }

    /// <summary>Slots in the group whose latest invite went to this address (case-insensitive).</summary>
    private static async Task<IReadOnlySet<MemberId>> SlotsInvitedAt(
        IDocumentSession session, GroupId groupId, string email, CancellationToken ct)
    {
        if (email.Length == 0)
            return new HashSet<MemberId>();

        // A group has a handful of deliveries; compare in memory rather than lean on
        // database collation for case-insensitivity.
        var deliveries = await session.Query<InviteDelivery>().Where(d => d.GroupId == groupId).ToListAsync(ct);
        return deliveries
            .Where(d => string.Equals(d.Email, email, StringComparison.OrdinalIgnoreCase))
            .Select(d => MemberId.From(d.Id))
            .ToHashSet();
    }
}
