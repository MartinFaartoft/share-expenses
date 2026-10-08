using Marten;

namespace SplitIt.Slices.EditExpense;

public static class EditExpenseSlice
{
    public static void Register(StoreOptions opts) =>
        opts.Events.MapEventType<ExpenseEdited>("expense_edited");
}
