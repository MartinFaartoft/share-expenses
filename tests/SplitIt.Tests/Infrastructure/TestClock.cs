namespace SplitIt.Tests.Infrastructure;

/// <summary>
/// A clock tests can move. Follows real time unless <see cref="Offset"/> is set,
/// so tests that do not care about time are unaffected.
/// </summary>
public sealed class TestClock : TimeProvider
{
    private long _offsetTicks;

    public TimeSpan Offset
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
        set => Interlocked.Exchange(ref _offsetTicks, value.Ticks);
    }

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
}
