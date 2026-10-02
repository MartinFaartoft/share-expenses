using ShareExpenses.Shared;

namespace ShareExpenses.Slices.ViewBalances;

/// <summary>The query, as in <c>event-model.yaml</c>.</summary>
/// <param name="UserId">The signed-in user: members only, and who "you" is.</param>
/// <param name="Now">The clock, passed in so reading stays pure: an invite is open until its deadline.</param>
internal sealed record Query(UserId UserId, DateTimeOffset Now);

/// <summary>The read model, as in <c>event-model.yaml</c>: exactly what the Balances screen receives.</summary>
/// <param name="You">The caller's own slot.</param>
/// <param name="Currency">ISO 4217; amounts are in its minor unit.</param>
internal sealed record GroupLedgerReadModel(
    string GroupName,
    string Currency,
    MemberId You,
    IReadOnlyList<LedgerMember> Members,
    IReadOnlyList<LedgerExpense> Expenses);

/// <param name="Status"><c>joined</c>, <c>invited</c> or <c>placeholder</c>.</param>
/// <param name="BalanceMinor">Positive is owed, negative owes (spec §9).</param>
internal sealed record LedgerMember(MemberId MemberId, string Name, string Status, long BalanceMinor);

internal sealed record LedgerExpense(
    ExpenseId ExpenseId,
    string Description,
    long AmountMinor,
    MemberId PayerMemberId,
    DateOnly PaidOn,
    ExpenseSplit Split,
    IReadOnlyList<Split> Splits);

/// <summary>Specs: <c>docs/event-model/slice-07-view-balances.md</c>.</summary>
internal static class Reader
{
    public const string Joined = "joined";
    public const string Invited = "invited";
    public const string Placeholder = "placeholder";

    /// <param name="state">The stored ledger, or null if the group does not exist.</param>
    /// <returns>Null — "not found" — for a missing group and a non-member alike.</returns>
    public static GroupLedgerReadModel? Read(State? state, Query query)
    {
        if (state?.Slots.FirstOrDefault(s => s.ClaimedBy == query.UserId) is not { } you)
            return null;

        var members = state.Slots
            .Select(s => new LedgerMember(s.MemberId, s.Name, StatusOf(s, query.Now), s.BalanceMinor))
            .ToList();

        // Newest first: by the day spent, then the most recently recorded.
        var expenses = state.Expenses
            .Select((e, recorded) => (e, recorded))
            .OrderByDescending(x => x.e.PaidOn)
            .ThenByDescending(x => x.recorded)
            .Select(x => new LedgerExpense(
                x.e.ExpenseId, x.e.Description, x.e.AmountMinor, x.e.PayerMemberId, x.e.PaidOn, x.e.Split, x.e.Splits))
            .ToList();

        return new GroupLedgerReadModel(state.GroupName, state.Currency, you.MemberId, members, expenses);
    }

    private static string StatusOf(Slot slot, DateTimeOffset now) =>
        slot.ClaimedBy is not null ? Joined
        : now < slot.InvitedUntil ? Invited
        : Placeholder;
}
