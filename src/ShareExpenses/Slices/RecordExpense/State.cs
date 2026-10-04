using System.Collections.Immutable;
using Marten.Schema;
using ShareExpenses.Shared;
using ShareExpenses.Slices.CreateGroup;

namespace ShareExpenses.Slices.RecordExpense;

/// <summary>
/// What RecordExpense needs to know about a group, folded from its stream by Marten
/// (<c>FetchForWriting</c>). No stream means no state: the group does not exist.
///
/// <see cref="Slots"/> keeps member-added order, which orders participants and splits
/// and decides who gets leftover minor units (spec §6). Expenses are not folded: no
/// rule here depends on earlier ones.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-06-record-expense.md:
///   MemberClaimReleased → remove from Members        (a released claim ends membership)
///   MemberRemoved       → drop the slot              (cannot pay or share)
///   GroupArchived / GroupUnarchived → track Archived (no changes to an archived group)
///
/// Internal until the slice has a screen: Wolverine will fetch it for the endpoint,
/// putting it in the endpoint's signature, so public (spec §12). The alias is
/// required: every slice has a State.
/// </summary>
[DocumentAlias("record_expense_state")]
internal sealed record State(ImmutableHashSet<UserId> Members, ImmutableList<MemberId> Slots)
{
    /// <summary>The stream id, set by Marten; Wolverine needs it to type the aggregate's identity.</summary>
    public GroupId Id { get; init; }

    public static State Create(GroupCreated _) => new([], []);

    public State Apply(MemberAdded e) => this with { Slots = Slots.Add(e.MemberId) };

    public State Apply(MemberClaimed e) => this with { Members = Members.Add(e.UserId) };
}
