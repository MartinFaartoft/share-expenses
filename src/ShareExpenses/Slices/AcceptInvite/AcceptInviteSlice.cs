using Marten;

namespace ShareExpenses.Slices.AcceptInvite;

/// <summary>Slice 5 — Accept invite. The slice's only public entry point (spec §12).</summary>
public static class AcceptInviteSlice
{
    /// <summary>
    /// Nothing to register: it emits <c>MemberClaimed</c>, owned and registered by
    /// CreateGroup, and its state is folded live by <c>FetchForWriting</c>.
    /// </summary>
    public static void Register(StoreOptions opts) { }

    public static void Map(IEndpointRouteBuilder api) => Endpoint.Map(api);
}
