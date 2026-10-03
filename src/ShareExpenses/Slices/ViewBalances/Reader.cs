using System.Text.Json.Serialization;
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
    GroupId GroupId,
    string GroupName,
    string Currency,
    MemberId You,
    IReadOnlyList<LedgerMember> Members,
    IReadOnlyList<LedgerEntry> History);

/// <param name="Status"><c>joined</c>, <c>invited</c> or <c>placeholder</c>.</param>
/// <param name="BalanceMinor">Positive is owed, negative owes (spec §9).</param>
internal sealed record LedgerMember(MemberId MemberId, string Name, string Status, long BalanceMinor);

/// <summary>
/// One entry in the money history, marked by <c>kind</c>: <c>expense</c> or <c>settlement</c>.
/// Only ever written, never read back, so System.Text.Json's built-in polymorphism
/// serves; it writes the discriminator because the list is typed as the base.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LedgerExpense), "expense")]
[JsonDerivedType(typeof(LedgerSettlement), "settlement")]
internal abstract record LedgerEntry(DateOnly PaidOn, long AmountMinor);

internal sealed record LedgerExpense(
    ExpenseId ExpenseId,
    string Description,
    long AmountMinor,
    MemberId PayerMemberId,
    DateOnly PaidOn,
    ExpenseSplit Split,
    IReadOnlyList<Split> Splits) : LedgerEntry(PaidOn, AmountMinor);

internal sealed record LedgerSettlement(
    SettlementId SettlementId,
    MemberId FromMemberId,
    MemberId ToMemberId,
    long AmountMinor,
    DateOnly PaidOn) : LedgerEntry(PaidOn, AmountMinor);

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

        // One history, newest first: by the day the money moved, then most recently recorded.
        var history = state.Expenses
            .Select(e => (e.Recorded, Entry: (LedgerEntry)new LedgerExpense(
                e.ExpenseId, e.Description, e.AmountMinor, e.PayerMemberId, e.PaidOn, e.Split, e.Splits)))
            .Concat(state.Settlements.Select(s => (s.Recorded, Entry: (LedgerEntry)new LedgerSettlement(
                s.SettlementId, s.FromMemberId, s.ToMemberId, s.AmountMinor, s.PaidOn))))
            .OrderByDescending(x => x.Entry.PaidOn)
            .ThenByDescending(x => x.Recorded)
            .Select(x => x.Entry)
            .ToList();

        return new GroupLedgerReadModel(GroupId.From(state.Id), state.GroupName, state.Currency, you.MemberId, members, history);
    }

    private static string StatusOf(Slot slot, DateTimeOffset now) =>
        slot.ClaimedBy is not null ? Joined
        : now < slot.InvitedUntil ? Invited
        : Placeholder;
}
