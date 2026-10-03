using ShareExpenses.Shared;

namespace ShareExpenses.Slices.ViewSettlementPlan;

/// <summary>The query: the signed-in user — members only, and who "you" is.</summary>
internal sealed record Query(UserId UserId);

/// <summary>The read model, as in <c>event-model.yaml</c>: what the Settle up screen shows.</summary>
/// <param name="Currency">ISO 4217; amounts are in its minor unit.</param>
/// <param name="You">The caller's own slot, so the screen can put their lines first.</param>
internal sealed record SettlementPlanReadModel(string Currency, MemberId You, IReadOnlyList<PlannedTransfer> Transfers);

/// <summary>One line of the plan, with both names, so the screen needs nothing else.</summary>
internal sealed record PlannedTransfer(MemberId FromMemberId, string FromName, MemberId ToMemberId, string ToName, long AmountMinor);

/// <summary>Specs: <c>docs/event-model/slice-09-view-settlement-plan.md</c>.</summary>
internal static class Reader
{
    /// <param name="state">The group's state, or null if its stream does not exist.</param>
    /// <returns>Null — "not found" — for a missing group and a non-member alike.</returns>
    public static SettlementPlanReadModel? Read(State? state, Query query)
    {
        if (state?.Slots.FirstOrDefault(s => s.ClaimedBy == query.UserId) is not { } you)
            return null;

        var names = state.Slots.ToDictionary(s => s.MemberId, s => s.Name);
        var plan = SettlementPlan.Plan([.. state.Slots.Select(s => (s.MemberId, s.BalanceMinor))], state.Score);

        return new SettlementPlanReadModel(
            state.Currency,
            you.MemberId,
            [.. plan.Select(t => new PlannedTransfer(t.From, names[t.From], t.To, names[t.To], t.AmountMinor))]);
    }
}
