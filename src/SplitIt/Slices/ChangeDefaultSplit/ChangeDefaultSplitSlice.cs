using Marten;

namespace SplitIt.Slices.ChangeDefaultSplit;

/// <summary>Change a group's default split: what it contributes to the store (spec §12).</summary>
public static class ChangeDefaultSplitSlice
{
    public static void Register(StoreOptions opts) =>
        // Explicit, stable name: survives a class or folder rename (spec §11).
        opts.Events.MapEventType<GroupDefaultSplitChanged>("group_default_split_changed");
}
