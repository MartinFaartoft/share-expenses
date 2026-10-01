using Marten;

namespace ShareExpenses.Tests.Specs;

/// <summary>
/// Given/When/Then against a real event stream, for scenarios that only the store
/// can decide (e.g. "created exactly once"). Same shape as <see cref="DecideSpec{TCommand}"/>:
/// <code>
/// await Spec.Given(pastEvents).When(command).Then(newEvents);
/// await Spec.Given(pastEvents).When(command).ThenRejected("reason");
/// </code>
/// Given appends the history to <paramref name="streamId"/>; Then asserts on the
/// stream's contents afterwards — appended events on success, untouched on rejection.
/// </summary>
/// <param name="handle">Runs the slice's handler; returns the rejection reason, or null on success.</param>
internal sealed class StreamSpec<TCommand>(
    IDocumentStore store, Guid streamId, Func<IDocumentSession, TCommand, Task<string?>> handle)
{
    public GivenStage Given(params object[] history) => new(this, history);

    internal sealed class GivenStage(StreamSpec<TCommand> spec, IReadOnlyList<object> history)
    {
        public WhenStage When(TCommand command) => new(spec, history, command);
    }

    internal sealed class WhenStage(StreamSpec<TCommand> spec, IReadOnlyList<object> history, TCommand command)
    {
        public async Task Then(params object[] expected)
        {
            var reason = await spec.Run(history, command);
            if (reason is not null)
                Assert.Fail($"Expected events, but the command was rejected: {reason}");
            Assert.Equal([.. history, .. expected], await spec.Stream());
        }

        public async Task ThenRejected(string reason)
        {
            Assert.Equal(reason, await spec.Run(history, command));
            Assert.Equal(history, await spec.Stream());
        }
    }

    private async Task<string?> Run(IReadOnlyList<object> history, TCommand command)
    {
        if (history.Count > 0)
        {
            await using var given = store.LightweightSession();
            given.Events.StartStream(streamId, history.ToArray());
            await given.SaveChangesAsync();
        }

        await using var when = store.LightweightSession();
        return await handle(when, command);
    }

    private async Task<IReadOnlyList<object>> Stream()
    {
        await using var session = store.QuerySession();
        return (await session.Events.FetchStreamAsync(streamId)).Select(e => e.Data).ToList();
    }
}
