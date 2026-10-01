using Marten;
using ShareExpenses.Slices.CreateGroup;

namespace ShareExpenses;

/// <summary>
/// The explicit list of slices (spec §12). Every slice under <c>Slices/</c> exposes a
/// public static <c>&lt;Name&gt;Slice</c> with <c>Register</c> and <c>Map</c>, and is called
/// from both methods below. The architecture tests fail if one is missing.
/// </summary>
public static class AllSlices
{
    /// <summary>Event types and projections, contributed by the slice that owns them.</summary>
    public static void Register(StoreOptions opts)
    {
        CreateGroupSlice.Register(opts);
    }

    /// <summary>HTTP endpoints, contributed by the slice that owns them.</summary>
    public static void Map(IEndpointRouteBuilder api)
    {
        CreateGroupSlice.Map(api);
    }
}
