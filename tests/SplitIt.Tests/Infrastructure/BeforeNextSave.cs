using Marten;
using Marten.Services;

namespace SplitIt.Tests.Infrastructure;

/// <summary>
/// A second phone, on cue: runs a competing write just before the next session save
/// anywhere in the app. Registered as a store-wide Marten listener, so it reaches
/// sessions the test never sees — such as the ones Wolverine opens for an endpoint.
/// One-shot: disarmed before the competitor runs, so the competitor's own save does
/// not trigger it again. Tests in the app collection run one at a time.
/// </summary>
public sealed class BeforeNextSave : DocumentSessionListenerBase
{
    private Func<IDocumentSession, Task>? _competitor;

    public void Arm(Func<Task> competitor) => _competitor = _ => competitor();

    /// <summary>The competitor sees the session about to save — e.g. to read which stream it starts.</summary>
    public void Arm(Func<IDocumentSession, Task> competitor) => _competitor = competitor;

    public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        var competitor = Interlocked.Exchange(ref _competitor, null);
        if (competitor is not null) await competitor(session);
    }
}
