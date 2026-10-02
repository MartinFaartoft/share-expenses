using Marten;

namespace ShareExpenses.Slices.InviteMember;

/// <summary>Invite a member: what it contributes to the store (spec §12).</summary>
public static class InviteMemberSlice
{
    public static void Register(StoreOptions opts) =>
        // Explicit, stable name: survives a class or folder rename (spec §11).
        opts.Events.MapEventType<MemberInvited>("member_invited");
}
