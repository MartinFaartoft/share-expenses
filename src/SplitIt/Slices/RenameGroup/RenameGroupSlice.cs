using Marten;

namespace SplitIt.Slices.RenameGroup;

/// <summary>Rename a group: what it contributes to the store (spec §12).</summary>
public static class RenameGroupSlice
{
    public static void Register(StoreOptions opts) =>
        opts.Events.MapEventType<GroupRenamed>("group_renamed");
}
