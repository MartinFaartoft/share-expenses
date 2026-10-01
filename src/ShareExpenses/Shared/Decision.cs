namespace ShareExpenses.Shared;

/// <summary>
/// The outcome of a slice's pure decide function: either the events to append, or
/// why the command was refused. Deciding never touches I/O.
/// </summary>
internal abstract record Decision
{
    private Decision() { }

    public sealed record Accepted(IReadOnlyList<object> Events) : Decision;

    /// <summary>
    /// Refused. <see cref="Kind"/> says what kind of refusal it is, so callers branch
    /// on the kind — never on the wording of <see cref="Reason"/>, which is free to
    /// change without changing behaviour.
    /// </summary>
    public sealed record Rejected(string Reason, Rejection Kind) : Decision;

    public static Decision Accept(params object[] events) => new Accepted(events);

    /// <summary>The command itself is wrong: bad input, or a rule it breaks (HTTP 400).</summary>
    public static Decision Reject(string reason) => new Rejected(reason, Rejection.Invalid);

    /// <summary>
    /// What the command targets does not exist — as far as this actor may know (HTTP 404).
    /// Also the answer for "exists, but not for you", so nothing can be probed.
    /// </summary>
    public static Decision NotFound(string reason) => new Rejected(reason, Rejection.NotFound);
}

internal enum Rejection
{
    Invalid,
    NotFound,
}
