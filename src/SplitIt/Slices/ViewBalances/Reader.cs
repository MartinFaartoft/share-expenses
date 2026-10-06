using SplitIt.Shared;

namespace SplitIt.Slices.ViewBalances;

/// <summary>The query, as in <c>event-model.yaml</c>.</summary>
/// <param name="UserId">The signed-in user: members only, and who "you" is.</param>
/// <param name="Now">The clock, passed in so reading stays pure: an invite is open until its deadline.</param>
internal sealed record Query(UserId UserId, DateTimeOffset Now);

/// <summary>
/// The read model, as in <c>event-model.yaml</c>: exactly what the balances page receives.
/// Public, with its parts, because the screen takes it as a component parameter (spec §3).
/// </summary>
/// <param name="You">The caller's own slot.</param>
/// <param name="Currency">ISO 4217; amounts are in its minor unit.</param>
public sealed record GroupBalancesReadModel(
    GroupId GroupId,
    string GroupName,
    string Currency,
    MemberId You,
    IReadOnlyList<MemberBalance> Members);

/// <param name="Status"><c>joined</c>, <c>invited</c> or <c>placeholder</c>.</param>
/// <param name="BalanceMinor">Positive is owed, negative owes (spec §9).</param>
public sealed record MemberBalance(MemberId MemberId, string Name, string Status, long BalanceMinor);

/// <summary>Specs: <c>docs/event-model/slice-08-view-balances.md</c>.</summary>
internal static class Reader
{
    public const string Joined = "joined";
    public const string Invited = "invited";
    public const string Placeholder = "placeholder";

    /// <param name="state">The folded group, or null if it does not exist.</param>
    /// <returns>Null — "not found" — for a missing group and a non-member alike.</returns>
    public static GroupBalancesReadModel? Read(State? state, Query query)
    {
        if (state?.Slots.FirstOrDefault(s => s.ClaimedBy == query.UserId) is not { } you)
            return null;

        var members = state.Slots
            .Select(s => new MemberBalance(s.MemberId, s.Name, StatusOf(s, query.Now), s.BalanceMinor))
            .ToList();

        return new GroupBalancesReadModel(GroupId.From(state.Id), state.GroupName, state.Currency, you.MemberId, members);
    }

    private static string StatusOf(Slot slot, DateTimeOffset now) =>
        slot.ClaimedBy is not null ? Joined
        : now < slot.InvitedUntil ? Invited
        : Placeholder;
}
