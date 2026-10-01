using Marten;
using Marten.Exceptions;
using ShareExpenses.Shared;

namespace ShareExpenses.Slices.CreateGroup;

internal abstract record Outcome
{
    private Outcome() { }

    public sealed record Created(Guid GroupId, Guid MemberId) : Outcome;

    public sealed record Invalid(string Reason) : Outcome;

    public sealed record AlreadyExists(Guid GroupId) : Outcome;
}

/// <summary>Decide, then start the stream. Ids come in from outside so tests can pin them.</summary>
internal static class Handler
{
    public static async Task<Outcome> Handle(
        IDocumentSession session, Command command, Guid groupId, Guid memberId, CancellationToken ct)
    {
        var decision = Decider.Decide(command, groupId, memberId);
        if (decision is Decision.Rejected rejected)
            return new Outcome.Invalid(rejected.Reason);
        var events = ((Decision.Accepted)decision).Events;

        // StartStream appends at expected version 0: if the stream already exists the
        // save fails, which is what guarantees a group is created exactly once.
        session.Events.StartStream(groupId, events.ToArray());
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (ExistingStreamIdCollisionException)
        {
            return new Outcome.AlreadyExists(groupId);
        }

        return new Outcome.Created(groupId, memberId);
    }
}
