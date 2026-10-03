using ShareExpenses.Shared;

namespace ShareExpenses.Slices.RecordSettlement;

// Owned by this slice: it is the first to emit it (spec §12). Additive changes
// only (spec §11): a change of shape is a new type plus an upcaster.

/// <summary>
/// <paramref name="FromMemberId"/> paid <paramref name="ToMemberId"/>
/// <paramref name="AmountMinor"/>, in the group currency's minor unit, on
/// <paramref name="PaidOn"/>; recorded by user <paramref name="By"/>, any member.
///
/// A transaction in the ledger with the balance effect of an expense paid by
/// <c>from</c> and shared by <c>to</c> alone — but its own fact, not an expense
/// (spec §7). <paramref name="PaidOn"/> is when the money moved, a domain fact; when it
/// was recorded is Marten metadata (spec §11).
/// </summary>
public sealed record SettlementRecorded(
    SettlementId SettlementId,
    MemberId FromMemberId,
    MemberId ToMemberId,
    long AmountMinor,
    DateOnly PaidOn,
    UserId By);
