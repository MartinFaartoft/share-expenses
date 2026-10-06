using Marten;

namespace SplitIt.Slices.RecordSettlement;

/// <summary>Record a settlement: what it contributes to the store (spec §12).</summary>
public static class RecordSettlementSlice
{
    public static void Register(StoreOptions opts) =>
        // Explicit, stable name: survives a class or folder rename (spec §11).
        opts.Events.MapEventType<SettlementRecorded>("settlement_recorded");
}
