namespace SplitIt.Tests.Specs;

public class FoldTests
{
    private sealed record Started(int Value);
    private sealed record Added(int Value);
    private sealed record Touched;
    private sealed record Unrelated;

    private sealed record Immutable(int Total)
    {
        public static Immutable Create(Started e) => new(e.Value);
        public Immutable Apply(Added e) => this with { Total = Total + e.Value };
    }

    private sealed class Mutable
    {
        public int Total { get; private set; }
        public int Touches { get; private set; }
        public static Mutable Create(Started e) => new() { Total = e.Value };
        public void Apply(Added e) => Total += e.Value;
        public void Apply(Touched _) => Touches++;
    }

    [Fact]
    public void Empty_history_is_no_state() =>
        Assert.Null(Fold.Of<Immutable>([]));

    [Fact]
    public void Creates_then_applies_returning_methods() =>
        Assert.Equal(new Immutable(6), Fold.Of<Immutable>([new Started(1), new Added(2), new Added(3)]));

    [Fact]
    public void Supports_mutating_apply_methods()
    {
        var state = Fold.Of<Mutable>([new Started(1), new Added(2), new Touched(), new Touched()])!;
        Assert.Equal((3, 2), (state.Total, state.Touches));
    }

    [Fact]
    public void An_event_the_state_does_not_fold_throws_and_names_both() =>
        Assert.Contains("does not fold Unrelated",
            Assert.Throws<InvalidOperationException>(() => Fold.Of<Immutable>([new Started(1), new Unrelated()])).Message);

    [Fact]
    public void Unless_the_spec_says_the_state_ignores_it() =>
        Assert.Equal(new Immutable(1), Fold.Of<Immutable>([new Started(1), new Unrelated()], typeof(Unrelated)));

    [Fact]
    public void Applying_before_creating_throws() =>
        Assert.Throws<InvalidOperationException>(() => Fold.Of<Immutable>([new Added(2)]));

    [Fact]
    public void The_states_own_exceptions_surface_unwrapped() =>
        Assert.Throws<KeyNotFoundException>(() => Fold.Of<Throwing>([new Started(1), new Added(1)]));

    private sealed record Throwing
    {
        public static Throwing Create(Started _) => new();
        public Throwing Apply(Added _) => throw new KeyNotFoundException();
    }
}
