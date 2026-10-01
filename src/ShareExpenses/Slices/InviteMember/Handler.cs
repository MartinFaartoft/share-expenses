using JasperFx;
using Marten;
using ShareExpenses.Shared;

namespace ShareExpenses.Slices.InviteMember;

internal abstract record Outcome
{
    private Outcome() { }

    /// <summary>Saved. Carries what the invite email needs to say.</summary>
    public sealed record Invited(string GroupName, string InviterName, string MemberName) : Outcome;

    public sealed record Invalid(string Reason) : Outcome;

    public sealed record GroupNotFound : Outcome;

    public sealed record MemberNotFound : Outcome;

    public sealed record Conflict : Outcome;
}

/// <summary>Fetch the group's state, decide, append at the version read, save.</summary>
internal static class Handler
{
    public static async Task<Outcome> Handle(IDocumentSession session, Command command, CancellationToken ct)
    {
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
        return new Outcome.Invited(state.GroupName, inviter, state.Slots[command.MemberId].Name);
    }
}
