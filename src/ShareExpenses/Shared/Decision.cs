namespace ShareExpenses.Shared;

/// <summary>
/// The outcome of a slice's pure decide function: either the events to append, or
/// the reason the command was refused. Deciding never touches I/O.
/// </summary>
internal abstract record Decision
{
    private Decision() { }

    public sealed record Accepted(IReadOnlyList<object> Events) : Decision;

    public sealed record Rejected(string Reason) : Decision;

    public static Decision Accept(params object[] events) => new Accepted(events);

    public static Decision Reject(string reason) => new Rejected(reason);
}
