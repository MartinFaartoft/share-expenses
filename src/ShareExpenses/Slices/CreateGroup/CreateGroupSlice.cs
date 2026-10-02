using Marten;

namespace ShareExpenses.Slices.CreateGroup;

/// <summary>Create group: what it contributes to the store (spec §12).</summary>
public static class CreateGroupSlice
{
    public static void Register(StoreOptions opts)
    {
        // Explicit, stable names: the stored name must survive a class or folder
        // rename, and is never reused for a different meaning (spec §11).
        opts.Events.MapEventType<GroupCreated>("group_created");
        opts.Events.MapEventType<MemberAdded>("member_added");
        opts.Events.MapEventType<MemberClaimed>("member_claimed");
    }
}
