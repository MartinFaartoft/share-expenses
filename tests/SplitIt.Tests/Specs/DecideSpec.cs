using SplitIt.Shared;

namespace SplitIt.Tests.Specs;

/// <summary>
/// Given/When/Then over a slice's pure decide function, mirroring the
/// <c>slice-NN-*.md</c> specifications:
/// <code>
/// Spec.Given(pastEvents).When(command).Then(newEvents);
/// Spec.Given().When(command).ThenRejected("reason");      // the command is invalid
/// Spec.Given().When(command).ThenNotFound("reason");      // its target does not exist (for this actor)
/// </code>
/// <c>Given()</c> with no arguments is the empty stream. The kind of rejection is
/// checked as well as its wording: it decides the HTTP status.
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

        public void ThenRejected(string reason) => ThenRefused(reason, Rejection.Invalid);

        public void ThenNotFound(string reason) => ThenRefused(reason, Rejection.NotFound);

        public void ThenAlreadyMember(string reason) => ThenRefused(reason, Rejection.AlreadyMember);

        public void ThenAlreadyRecorded(string reason) => ThenRefused(reason, Rejection.AlreadyRecorded);

        private void ThenRefused(string reason, Rejection kind)
        {
            if (decision is Decision.Accepted accepted)
                Assert.Fail($"Expected rejection '{reason}', but got events: {string.Join(", ", accepted.Events)}");
            Assert.Equal(new Decision.Rejected(reason, kind), decision);
        }
    }
}
