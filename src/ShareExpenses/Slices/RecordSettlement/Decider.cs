using ShareExpenses.Shared;

namespace ShareExpenses.Slices.RecordSettlement;

/// <summary>The command, as in <c>event-model.yaml</c>. Inputs are raw; deciding validates them.</summary>
/// <param name="SettlementId">The new settlement's id, chosen by the caller.</param>
/// <param name="AmountMinor">In the group currency's minor unit.</param>
/// <param name="Now">The clock, read by the caller, so deciding stays pure and testable.</param>
internal sealed record Command(
    SettlementId SettlementId,
    MemberId? FromMemberId,
    MemberId? ToMemberId,
    long AmountMinor,
    DateOnly? PaidOn,
    DateTimeOffset Now,
    UserId By);

/// <summary>Specs: <c>docs/event-model/slice-08-record-settlement.md</c>.</summary>
internal static class Decider
{
    /// <summary>The single answer for "no such group" and "not a member".</summary>
    public const string GroupNotFound = "group not found";

    /// <summary>As for an expense (spec §6).</summary>
    public const long MaxAmountMinor = 1_000_000_000_000;

    public static Decision Decide(State? state, Command command)
    {
        // Membership first: a non-member learns nothing, not even from validation.
        if (state is null || !state.Members.Contains(command.By))
            return Decision.NotFound(GroupNotFound);

        if (command.FromMemberId is not { } from || !state.Slots.Contains(from))
            return Decision.Reject("payer is not a member of the group");
        if (command.ToMemberId is not { } to || !state.Slots.Contains(to))
            return Decision.Reject("recipient is not a member of the group");
        if (from == to)
            return Decision.Reject("a member cannot pay themselves");

        // Not a rule: that from owes to. Balances absorb any payment (spec §7).
        if (command.AmountMinor <= 0)
            return Decision.Reject("amount must be positive");
        if (command.AmountMinor > MaxAmountMinor)
            return Decision.Reject("amount is too large");

        if (command.PaidOn is not { } paidOn)
            return Decision.Reject("date is required");
        // As for an expense: up to a day ahead of UTC, any earlier date.
        if (paidOn > DateOnly.FromDateTime(command.Now.UtcDateTime).AddDays(1))
            return Decision.Reject("date cannot be in the future");

        return Decision.Accept(new SettlementRecorded(
            command.SettlementId, from, to, command.AmountMinor, paidOn, command.By));
    }
}
