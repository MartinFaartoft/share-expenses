// Fixture slices for SliceBoundaryTests: compiled into the test assembly so every
// rule can be shown to pass on a conforming layout and fire on a violating one.
// The rules read IL, so these types only need to compile, never to run.

using Marten;
using Marten.Schema;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

// ── Conforming ────────────────────────────────────────────────────────────────

namespace ShareExpenses.Tests.Architecture.Fixtures.Good.Slices.Alpha
{
    public sealed record AlphaHappened(Guid Id);

    [DocumentAlias("alpha_state")]
    internal sealed record State(int Count)
    {
        public State Apply(AlphaHappened _) => this with { Count = Count + 1 };
    }

    public static class AlphaSlice
    {
        public static void Register(StoreOptions opts) => opts.Events.AddEventType<AlphaHappened>();
        public static void Map(IEndpointRouteBuilder api) => api.MapGet("/alpha", () => new State(0).Count);
    }
}

namespace ShareExpenses.Tests.Architecture.Fixtures.Good.Slices.Beta
{
    using ShareExpenses.Tests.Architecture.Fixtures.Good.Slices.Alpha;

    // Folding another slice's public event is the intended coupling.
    [DocumentAlias("beta_state")]
    internal sealed record State(int AlphaCount)
    {
        public State Apply(AlphaHappened _) => this with { AlphaCount = AlphaCount + 1 };
    }

    public static class BetaSlice
    {
        public static void Register(StoreOptions opts) { }
        public static void Map(IEndpointRouteBuilder api) => api.MapGet("/beta", () => new State(0).AlphaCount);
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
        public static void Register(StoreOptions opts)
        {
            Slices.Alpha.AlphaSlice.Register(opts);
            Slices.Beta.BetaSlice.Register(opts);
        }

        public static void Map(IEndpointRouteBuilder api)
        {
            Slices.Alpha.AlphaSlice.Map(api);
            Slices.Beta.BetaSlice.Map(api);
        }
    }
}

// ── Violating ─────────────────────────────────────────────────────────────────

namespace ShareExpenses.Tests.Architecture.Fixtures.Bad.Slices.Gamma
{
    public sealed record GammaHappened(Guid Id);

    public class Leaky;                        // public, not a record

    public record Unsealed(int X);             // public record, not sealed

    internal sealed record State(int X);       // no [DocumentAlias]

    public static class GammaSlice
    {
        public static void Register(StoreOptions opts) { }
        public static void Map(IEndpointRouteBuilder api) { }
    }
}

namespace ShareExpenses.Tests.Architecture.Fixtures.Bad.Slices.Delta
{
    [DocumentAlias("shared_state")]
    internal sealed record State(int Y);       // alias also used by Epsilon

    internal static class Peek
    {
        public static int Look(Gamma.State state) => state.X;   // another slice's internal
    }

    public static class DeltaSlice
    {
        public static void Register(StoreOptions opts) { }
        public static void Map(IEndpointRouteBuilder api) { } // never called by the catalog
    }
}

namespace ShareExpenses.Tests.Architecture.Fixtures.Bad.Slices.Epsilon
{
    internal sealed record Orphan(int X);      // slice with no entry point

    [DocumentAlias("shared_state")]
    internal sealed record State(int Z);       // alias also used by Delta
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
        public static void Register(StoreOptions opts)
        {
            Slices.Gamma.GammaSlice.Register(opts);
            Slices.Delta.DeltaSlice.Register(opts);
        }

        public static void Map(IEndpointRouteBuilder api)
        {
            Slices.Gamma.GammaSlice.Map(api);
        }
    }
}
