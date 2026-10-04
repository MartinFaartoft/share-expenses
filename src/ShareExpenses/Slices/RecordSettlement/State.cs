using System.Collections.Immutable;
using Marten.Schema;
using ShareExpenses.Shared;
using ShareExpenses.Slices.CreateGroup;

namespace ShareExpenses.Slices.RecordSettlement;

/// <summary>
/// What RecordSettlement needs to know about a group, folded from its stream by Marten
/// (<c>FetchForWriting</c>). No stream means no state: the group does not exist.
/// Balances are not folded: no rule depends on them — any payment is valid (spec §7).
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-09-record-settlement.md:
///   MemberClaimReleased → remove from Members        (a released claim ends membership)
///   MemberRemoved       → drop the slot              (cannot pay or be paid)
///   GroupArchived / GroupUnarchived → track Archived (no changes to an archived group)
///
/// Internal until the slice has a screen: Wolverine will fetch it for the endpoint,
/// putting it in the endpoint's signature, so public (spec §12). The alias is
/// required: every slice has a State.
/// </summary>
[DocumentAlias("record_settlement_state")]
internal sealed record State(ImmutableHashSet<UserId> Members, ImmutableHashSet<MemberId> Slots)
{
    /// <summary>The stream id, set by Marten; Wolverine needs it to type the aggregate's identity.</summary>
    public GroupId Id { get; init; }

    public static State Create(GroupCreated _) => new([], []);

    public State Apply(MemberAdded e) => this with { Slots = Slots.Add(e.MemberId) };

    public State Apply(MemberClaimed e) => this with { Members = Members.Add(e.UserId) };
}
