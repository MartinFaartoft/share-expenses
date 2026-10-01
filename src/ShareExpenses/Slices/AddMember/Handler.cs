using Marten;
using JasperFx;
using ShareExpenses.Shared;

namespace ShareExpenses.Slices.AddMember;

internal abstract record Outcome
{
    private Outcome() { }

    public sealed record Added(MemberId MemberId) : Outcome;

    public sealed record Invalid(string Reason) : Outcome;

    public sealed record NotFound : Outcome;

    public sealed record Conflict : Outcome;
}

/// <summary>Fetch the group's state, decide, append at the version read, save.</summary>
internal static class Handler
{
    public static async Task<Outcome> Handle(
        IDocumentSession session, Command command, MemberId memberId, CancellationToken ct)
    {
        var stream = await session.Events.FetchForWriting<State>(command.GroupId.Value, ct);

        switch (Decider.Decide(stream.Aggregate, command, memberId))
        {
            case Decision.Rejected { Reason: Decider.GroupNotFound }:
                return new Outcome.NotFound();
            case Decision.Rejected rejected:
                return new Outcome.Invalid(rejected.Reason);
            case Decision.Accepted accepted:
                stream.AppendMany(accepted.Events);
                break;
        }

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (ConcurrencyException)
        {
            // DECISION (slice 2): on a concurrency conflict the CLIENT retries — we
            // return 409 and do nothing else. This is spec §11 as written ("the loser
            // retries"), and keeps the conflict visible while learning.
            //
            // Revisit: a server-side retry (re-fetch, re-decide, re-append, a few
            // times) would be correct, not a blind overwrite — the rules run again
            // against the new state, so two phones adding "Bob" still yields one
            // "already exists". It would turn most 409s into a single round trip,
            // and belongs in Shared/ for every state-change slice to use.
            return new Outcome.Conflict();
        }

        return new Outcome.Added(memberId);
    }
}
