using Marten;

namespace ShareExpenses.Slices.InviteMember;

/// <summary>Slice 3 — Invite a member. The slice's only public entry point (spec §12).</summary>
public static class InviteMemberSlice
{
    public static void Register(StoreOptions opts) =>
        // Explicit, stable name: survives a class or folder rename (spec §11).
        opts.Events.MapEventType<MemberInvited>("member_invited");

    public static void Map(IEndpointRouteBuilder api) => Endpoint.Map(api);
}
