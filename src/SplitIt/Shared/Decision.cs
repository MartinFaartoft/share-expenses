namespace SplitIt.Shared;

internal abstract record Decision
{
    private Decision() { }

    public sealed record Accepted(IReadOnlyList<object> Events) : Decision;
    
    public sealed record Rejected(string Reason, Rejection Kind) : Decision;

    public static Decision Accept(params object[] events) => new Accepted(events);

    public static Decision Reject(string reason) => new Rejected(reason, Rejection.Invalid);

    public static Decision NotFound(string reason) => new Rejected(reason, Rejection.NotFound);

    public static Decision AlreadyMember(string reason) => new Rejected(reason, Rejection.AlreadyMember);

    public static Decision AlreadyRecorded(string reason) => new Rejected(reason, Rejection.AlreadyRecorded);
}

internal enum Rejection
{
    Invalid,
    NotFound,
    AlreadyMember,
    AlreadyRecorded,
}
