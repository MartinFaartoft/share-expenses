using Marten;

namespace SplitIt.Slices.RecordExpense;

/// <summary>Record an expense: what it contributes to the store (spec §12).</summary>
public static class RecordExpenseSlice
{
    public static void Register(StoreOptions opts) =>
        // Explicit, stable name: survives a class or folder rename (spec §11).
        opts.Events.MapEventType<ExpenseRecorded>("expense_recorded");
}
