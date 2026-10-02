using JasperFx;
using Marten;
using ShareExpenses.Infrastructure.Invites;
using ShareExpenses.Shared;
using ShareExpenses.Slices.CreateGroup;

namespace ShareExpenses.Slices.ClaimMember;

internal abstract record Outcome
{
    private Outcome() { }

    public sealed record Claimed(MemberId MemberId) : Outcome;

    public sealed record NotFound(string Reason) : Outcome;

    /// <summary>The user already holds <paramref name="MemberId"/>; the client can go straight there.</summary>
    public sealed record AlreadyMember(string Reason, MemberId MemberId) : Outcome;

    public sealed record Conflict : Outcome;
}

/// <summary>
/// Fetch the group's state, decide, then append the claim and drop the slot's invite
/// delivery — one session, one transaction. The address is not needed once claimed.
/// </summary>
internal static class Handler
{
    public static async Task<Outcome> Handle(IDocumentSession session, Command command, CancellationToken ct)
    {
        var stream = await session.Events.FetchForWriting<State>(command.GroupId.Value, ct);
        var state = stream.Aggregate;

        MemberId claimed;
        switch (Decider.Decide(state, command))
        {
            case Decision.Rejected { Kind: Rejection.AlreadyMember } rejected:
                return new Outcome.AlreadyMember(rejected.Reason, state!.Members[command.UserId]);
            case Decision.Rejected rejected:
                return new Outcome.NotFound(rejected.Reason);
            case Decision.Accepted accepted:
                stream.AppendMany(accepted.Events);
                claimed = accepted.Events.OfType<MemberClaimed>().Single().MemberId;
                session.Delete<InviteDelivery>(claimed.Value);
                break;
            default:
                throw new InvalidOperationException("Unhandled decision");
        }

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (ConcurrencyException)
        {
            // Client retries on 409, as in AddMember (spec §11). Two people racing on
            // one link: the retry finds the invite used, and answers "not found".
            return new Outcome.Conflict();
        }

        return new Outcome.Claimed(claimed);
    }
}
