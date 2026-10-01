using Marten;

namespace ShareExpenses.Slices.ViewInvite;

/// <summary>Slice 4 — View invite. The slice's only public entry point (spec §12).</summary>
public static class ViewInviteSlice
{
    /// <summary>
    /// Nothing to register: it emits no events, and its read model is folded live per
    /// request rather than projected and stored.
    /// </summary>
    public static void Register(StoreOptions opts) { }

    public static void Map(IEndpointRouteBuilder api) => Endpoint.Map(api);
}
