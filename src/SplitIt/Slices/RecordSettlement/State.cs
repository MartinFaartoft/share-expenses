using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.ArchiveGroup;

namespace SplitIt.Slices.RecordSettlement;

/// <summary>
/// What RecordSettlement needs to know about a group, folded from its stream by Marten
/// (<c>FetchForWriting</c>). No stream means no state: the group does not exist.
/// Balances are not folded: no rule depends on them — any payment is valid (spec §7).
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-09-record-settlement.md:
///   MemberClaimReleased → remove from Members        (a released claim ends membership)
///   MemberRemoved       → drop the slot              (cannot pay or be paid)
///   GroupUnarchived     → clear Archived (deferred, spec §11)
///
/// Public: Wolverine fetches it for the endpoint, so it is in the endpoint's signature
/// (spec §12). The alias is required: every slice has a State.
/// </summary>
/// <param name="Currency">To read the amount a form posts, in the currency's decimals.</param>
/// <param name="Settlements">Those recorded, so one form submitted twice records one.</param>
[DocumentAlias("record_settlement_state")]
public sealed record State(
    ImmutableHashSet<UserId> Members,
    ImmutableHashSet<MemberId> Slots,
    string Currency,
    ImmutableHashSet<SettlementId> Settlements)
{
    /// <summary>The stream id, set by Marten; Wolverine needs it to type the aggregate's identity.</summary>
    public GroupId Id { get; init; }

    /// <summary>Whether the group is archived: it takes no command (slice-15-archive-group.md).</summary>
    public bool Archived { get; init; }

    public static State Create(GroupCreated e) => new([], [], e.Currency, []);

    public State Apply(GroupArchived e) => this with { Archived = true };

    public State Apply(MemberAdded e) => this with { Slots = Slots.Add(e.MemberId) };

    public State Apply(MemberClaimed e) => this with { Members = Members.Add(e.UserId) };

    public State Apply(SettlementRecorded e) => this with { Settlements = Settlements.Add(e.SettlementId) };
}
