using ShareExpenses.Shared;

namespace ShareExpenses.Tests.Specs;

/// <summary>
/// Given/When/Then over a slice's pure decide function, mirroring the
/// <c>slice-NN-*.md</c> specifications:
/// <code>
/// Spec.Given(pastEvents).When(command).Then(newEvents);
/// Spec.Given().When(command).ThenRejected("reason");
/// </code>
/// <c>Given()</c> with no arguments is the empty stream.
/// </summary>
/// <param name="decide">Folds the history into the slice's state and decides the command.</param>
internal sealed class DecideSpec<TCommand>(Func<IReadOnlyList<object>, TCommand, Decision> decide)
{
    public GivenStage Given(params object[] history) => new(decide, history);

    internal sealed class GivenStage(Func<IReadOnlyList<object>, TCommand, Decision> decide, IReadOnlyList<object> history)
    {
        public WhenStage When(TCommand command) => new(decide(history, command));
    }

    internal sealed class WhenStage(Decision decision)
    {
        public void Then(params object[] expected)
        {
            if (decision is Decision.Rejected rejected)
                Assert.Fail($"Expected events, but the command was rejected: {rejected.Reason}");
            Assert.Equal(expected, ((Decision.Accepted)decision).Events);
        }

        public void ThenRejected(string reason)
        {
            if (decision is Decision.Accepted accepted)
                Assert.Fail($"Expected rejection '{reason}', but got events: {string.Join(", ", accepted.Events)}");
            Assert.Equal(reason, ((Decision.Rejected)decision).Reason);
        }
    }
}
