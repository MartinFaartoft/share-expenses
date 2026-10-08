using System.Text.Json.Serialization;
using SplitIt.Shared;

namespace SplitIt.Slices.ViewGroup;

/// <summary>The query, as in <c>event-model.yaml</c>.</summary>
/// <param name="UserId">The signed-in user: members only, and who "you" is.</param>
internal sealed record Query(UserId UserId);

/// <summary>
/// The read model, as in <c>event-model.yaml</c>: exactly what the group page receives.
/// Public, with its parts, because the screen takes it as a component parameter, and
/// component parameters must be public (spec §3).
/// </summary>
/// <param name="You">The caller's own slot.</param>
/// <param name="Currency">ISO 4217; amounts are in its minor unit.</param>
/// <param name="Archived">Read-only: a banner, and no actions (slice-15-archive-group.md).</param>
/// <param name="BalanceMinor">The caller's balance: positive is owed, negative owes (spec §9).</param>
public sealed record GroupActivityReadModel(
    GroupId GroupId,
    string GroupName,
    string Currency,
    MemberId You,
    long BalanceMinor,
    IReadOnlyList<ActivityEntry> History,
    bool Archived);

/// <summary>
/// One entry in the money history, marked by <c>kind</c>: <c>expense</c> or <c>settlement</c>.
/// Only ever written, never read back, so System.Text.Json's built-in polymorphism
/// serves; it writes the discriminator because the list is typed as the base.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ActivityExpense), "expense")]
[JsonDerivedType(typeof(ActivitySettlement), "settlement")]
public abstract record ActivityEntry(DateOnly PaidOn, long AmountMinor);

public sealed record ActivityExpense(
    ExpenseId ExpenseId,
    string Description,
    long AmountMinor,
    MemberId PayerMemberId,
    string PayerName,
    DateOnly PaidOn,
    ExpenseSplit Split,
    IReadOnlyList<Split> Splits) : ActivityEntry(PaidOn, AmountMinor);

public sealed record ActivitySettlement(
    SettlementId SettlementId,
    MemberId FromMemberId,
    string FromName,
    MemberId ToMemberId,
    string ToName,
    long AmountMinor,
    DateOnly PaidOn) : ActivityEntry(PaidOn, AmountMinor);

/// <summary>Specs: <c>docs/event-model/slice-07-view-group.md</c>.</summary>
internal static class Reader
{
    /// <param name="state">The stored activity, or null if the group does not exist.</param>
    /// <returns>Null — "not found" — for a missing group and a non-member alike.</returns>
    public static GroupActivityReadModel? Read(State? state, Query query)
    {
        if (state?.Slots.FirstOrDefault(s => s.ClaimedBy == query.UserId) is not { } you)
            return null;

        string NameOf(MemberId member) => state.Slots.Single(s => s.MemberId == member).Name;

        // One history, newest first: by the day the money moved, then most recently recorded.
        var history = state.Expenses
            .Select(e => (e.Recorded, Entry: (ActivityEntry)new ActivityExpense(
                e.ExpenseId, e.Description, e.AmountMinor, e.PayerMemberId, NameOf(e.PayerMemberId), e.PaidOn, e.Split, e.Splits)))
            .Concat(state.Settlements.Select(s => (s.Recorded, Entry: (ActivityEntry)new ActivitySettlement(
                s.SettlementId, s.FromMemberId, NameOf(s.FromMemberId), s.ToMemberId, NameOf(s.ToMemberId), s.AmountMinor, s.PaidOn))))
            .OrderByDescending(x => x.Entry.PaidOn)
            .ThenByDescending(x => x.Recorded)
            .Select(x => x.Entry)
            .ToList();

        return new GroupActivityReadModel(
            GroupId.From(state.Id), state.GroupName, state.Currency, you.MemberId, you.BalanceMinor, history, state.Archived);
    }
}
