using Marten;

namespace ShareExpenses.Slices.AddMember;

/// <summary>Slice 2 — Add a member. The slice's only public entry point (spec §12).</summary>
public static class AddMemberSlice
{
    /// <summary>
    /// Nothing to register: it emits <c>MemberAdded</c>, owned and registered by
    /// CreateGroup, and its state is folded live by <c>FetchForWriting</c>.
    /// </summary>
    public static void Register(StoreOptions opts) { }

    public static void Map(IEndpointRouteBuilder api) => Endpoint.Map(api);
}
