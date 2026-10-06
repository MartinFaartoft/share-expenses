using SplitIt.Shared;

namespace SplitIt.Slices.RemoveExpense;

internal sealed record Command(ExpenseId ExpenseId, UserId By);

/// <summary>Specs: <c>docs/event-model/slice-11-remove-expense.md</c>.</summary>
internal static class Decider
{
    public const string GroupNotFound = "group not found";

    public static Decision Decide(State? state, Command command)
    {
        if (state is null || !state.Members.ContainsKey(command.By))
            return Decision.NotFound(GroupNotFound);

        if (!state.Expenses.ContainsKey(command.ExpenseId))
            return Decision.NotFound("expense not found");

        if (state.Removed.Contains(command.ExpenseId))
            return Decision.AlreadyRemoved("expense already removed");

        return Decision.Accept(new ExpenseRemoved(command.ExpenseId, command.By));
    }
}
