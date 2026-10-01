namespace ShareExpenses.Tests.Specs;

/// <summary>
/// Given/When/Then over a State Read slice's pure read function, mirroring the
/// <c>slice-NN-*.md</c> specifications — the read-side sibling of <see cref="DecideSpec{TCommand}"/>:
/// <code>
/// Spec.Given(pastEvents).When(query).Then(expectedReadModel);
/// Spec.Given(pastEvents).When(query).ThenNotFound();
/// </code>
/// </summary>
/// <param name="read">Folds the history into the slice's state and answers the query; null is "not found".</param>
internal sealed class ReadSpec<TQuery, TResult>(Func<IReadOnlyList<object>, TQuery, TResult?> read)
    where TResult : class
{
    public GivenStage Given(params object[] history) => new(read, history);

    internal sealed class GivenStage(Func<IReadOnlyList<object>, TQuery, TResult?> read, IReadOnlyList<object> history)
    {
        public WhenStage When(TQuery query) => new(read(history, query));
    }

    internal sealed class WhenStage(TResult? result)
    {
        public void Then(TResult expected)
        {
            if (result is null)
                Assert.Fail($"Expected {expected}, but found nothing");
            Assert.Equal(expected, result);
        }

        public void ThenNotFound()
        {
            if (result is not null)
                Assert.Fail($"Expected nothing to be found, but found {result}");
        }
    }
}
