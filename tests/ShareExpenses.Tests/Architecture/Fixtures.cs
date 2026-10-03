// Fixture slices for SliceBoundaryTests: compiled into the test assembly so every
// rule can be shown to pass on a conforming layout and fire on a violating one.
// The IL rules only need these to compile; the event-registration rule also calls
// each catalog's Register.

using Marten;
using Marten.Schema;
using Microsoft.AspNetCore.Routing;
using Wolverine.Http;

// ── Conforming ────────────────────────────────────────────────────────────────

namespace ShareExpenses.Tests.Architecture.Fixtures.Good.Slices.Alpha
{
    public sealed record AlphaHappened(Guid Id);

    [DocumentAlias("alpha_state")]
    internal sealed record State(int Count)
    {
        public State Apply(AlphaHappened _) => this with { Count = Count + 1 };
    }

    // Owns an event, so it has an entry point that registers it.
    public static class AlphaSlice
    {
        public static void Register(StoreOptions opts) => opts.Events.MapEventType<AlphaHappened>("alpha_happened");
    }
}

namespace ShareExpenses.Tests.Architecture.Fixtures.Good.Slices.Beta
{
    using ShareExpenses.Tests.Architecture.Fixtures.Good.Slices.Alpha;

    // Folding another slice's public event is the intended coupling. Public, with
    // its nested type, because it is in the endpoint's signature.
    [DocumentAlias("beta_state")]
    public sealed record State(Tally Tally)
    {
        public State Apply(AlphaHappened _) => this with { Tally = new Tally(Tally.Count + 1) };
    }

    public sealed record Tally(int Count);

    public sealed record Request(int Add);

    // Its screen: a component, public because Razor generates it so.
    public class BetaScreen : Microsoft.AspNetCore.Components.ComponentBase;

    // A slice on Wolverine with no events: an endpoint, its contract types, and no entry point.
    public static class Endpoint
    {
        [WolverinePost("/beta")]
        public static int Post(Request request, State state) => state.Tally.Count + request.Add;
    }
}

namespace ShareExpenses.Tests.Architecture.Fixtures.Good.Shared
{
    public static class Arithmetic
    {
        public static long Half(long minor) => minor / 2;
    }
}

namespace ShareExpenses.Tests.Architecture.Fixtures.Good
{
    public static class Catalog
    {
        public static void Register(StoreOptions opts) => Slices.Alpha.AlphaSlice.Register(opts);
    }
}

// ── Violating ─────────────────────────────────────────────────────────────────

namespace ShareExpenses.Tests.Architecture.Fixtures.Bad.Slices.Gamma
{
    public sealed record GammaHappened(Guid Id);   // an event its Register never maps

    public class Leaky;                        // public, not a record

    public record Unsealed(int X);             // public record, not sealed

    public static class Endpoint               // public, but no Wolverine route on it
    {
        public static int Post() => 0;
    }

    public sealed record GammaRequest(int X);  // public because the endpoint below takes it

    public static class Endpoints
    {
        [WolverinePost("/gamma")]
        public static int Post(GammaRequest request) => request.X;
    }

    internal sealed record State(int X);       // no [DocumentAlias]

    public static class GammaSlice
    {
        public static void Register(StoreOptions opts) { }
    }
}

namespace ShareExpenses.Tests.Architecture.Fixtures.Bad.Slices.Delta
{
    [DocumentAlias("shared_state")]
    internal sealed record State(int Y);       // alias also used by Epsilon

    internal static class Peek
    {
        public static int Look(Gamma.State state) => state.X;           // another slice's internal
        public static int Borrow(Gamma.GammaRequest request) => request.X; // public, but not an event
    }

    public static class DeltaSlice
    {
        public static void Register(StoreOptions opts) { }   // never called by the catalog
    }
}

namespace ShareExpenses.Tests.Architecture.Fixtures.Bad.Slices.Epsilon
{
    [DocumentAlias("shared_state")]
    internal sealed record State(int Z);       // alias also used by Delta

    public static class EpsilonSlice           // an entry point with no Register
    {
        public static void Map(IEndpointRouteBuilder api) { }
    }
}

namespace ShareExpenses.Tests.Architecture.Fixtures.Bad.Shared
{
    public static class Impure
    {
        public static Guid Leak(Slices.Gamma.GammaHappened e) => e.Id;   // depends on a slice
        public static void Route(IEndpointRouteBuilder api) { }          // depends on ASP.NET
    }
}

namespace ShareExpenses.Tests.Architecture.Fixtures.Bad
{
    public static class Catalog
    {
        public static void Register(StoreOptions opts) => Slices.Gamma.GammaSlice.Register(opts);
    }
}
