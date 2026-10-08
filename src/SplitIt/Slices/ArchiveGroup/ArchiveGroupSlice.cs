using Marten;

namespace SplitIt.Slices.ArchiveGroup;

/// <summary>Archive a group: what it contributes to the store (spec §12).</summary>
public static class ArchiveGroupSlice
{
    public static void Register(StoreOptions opts) =>
        // Explicit, stable name: survives a class or folder rename (spec §11).
        opts.Events.MapEventType<GroupArchived>("group_archived");
}
