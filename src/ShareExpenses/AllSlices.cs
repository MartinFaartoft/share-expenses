using Marten;
using ShareExpenses.Slices.AddMember;
using ShareExpenses.Slices.CreateGroup;
using ShareExpenses.Slices.InviteMember;
using ShareExpenses.Slices.ViewInvite;

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
        AddMemberSlice.Register(opts);
        InviteMemberSlice.Register(opts);
        ViewInviteSlice.Register(opts);
    }

    /// <summary>HTTP endpoints, contributed by the slice that owns them.</summary>
    public static void Map(IEndpointRouteBuilder api)
    {
        CreateGroupSlice.Map(api);
        AddMemberSlice.Map(api);
        InviteMemberSlice.Map(api);
        ViewInviteSlice.Map(api);
    }
}
