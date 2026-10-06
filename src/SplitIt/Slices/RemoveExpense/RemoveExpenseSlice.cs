using Marten;

namespace SplitIt.Slices.RemoveExpense;

public static class RemoveExpenseSlice
{
    public static void Register(StoreOptions opts) =>
        opts.Events.MapEventType<ExpenseRemoved>("expense_removed");
}
