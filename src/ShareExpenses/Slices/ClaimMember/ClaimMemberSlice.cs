using Marten;

namespace ShareExpenses.Slices.ClaimMember;

/// <summary>Slice 5 — Claim a slot. The slice's only public entry point (spec §12).</summary>
public static class ClaimMemberSlice
{
    /// <summary>
    /// Nothing to register: it emits <c>MemberClaimed</c>, owned and registered by
    /// CreateGroup, and its state is folded live by <c>FetchForWriting</c>.
    /// </summary>
    public static void Register(StoreOptions opts) { }

    public static void Map(IEndpointRouteBuilder api) => Endpoint.Map(api);
}
