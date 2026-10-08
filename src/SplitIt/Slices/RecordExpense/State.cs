using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.RenameGroup;

namespace SplitIt.Slices.RecordExpense;

/// <summary>
/// What RecordExpense needs to know about a group, folded from its stream by Marten.
/// No stream means no state: the group does not exist. The Add expense screen is built
/// from it as well as decided against.
///
/// <see cref="Slots"/> keeps member-added order, which orders participants and splits
/// and decides who gets leftover minor units (spec §6). Expenses are not folded, only
/// their ids: no rule here depends on earlier ones beyond a repeat.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-06-record-expense.md:
///   MemberClaimReleased → remove from Members        (a released claim ends membership)
///   MemberRemoved       → drop the slot              (cannot pay or share)
///   MemberRenamed       → rename in Names
///   GroupArchived / GroupUnarchived → track Archived (no changes to an archived group)
///
/// Public: Wolverine fetches it for the endpoint (spec §12). The alias is required:
/// every slice has a State.
/// </summary>
/// <param name="Members">The slot each user holds.</param>
/// <param name="Expenses">The ids recorded, so a form submitted twice records once.</param>
[DocumentAlias("record_expense_state")]
public sealed record State(
    string GroupName,
    string Currency,
    ImmutableList<MemberId> Slots,
    ImmutableDictionary<MemberId, string> Names,
    ImmutableDictionary<UserId, MemberId> Members,
    ImmutableHashSet<ExpenseId> Expenses)
{
    /// <summary>The stream id, set by Marten; Wolverine needs it to type the aggregate's identity.</summary>
    public GroupId Id { get; init; }

    public static State Create(GroupCreated e) =>
        new(e.Name, e.Currency, [], ImmutableDictionary<MemberId, string>.Empty,
            ImmutableDictionary<UserId, MemberId>.Empty, []);

    public State Apply(GroupRenamed e) => this with { GroupName = e.Name };

    public State Apply(MemberAdded e) =>
        this with { Slots = Slots.Add(e.MemberId), Names = Names.SetItem(e.MemberId, e.DisplayName) };

    public State Apply(MemberClaimed e) => this with { Members = Members.SetItem(e.UserId, e.MemberId) };

    public State Apply(ExpenseRecorded e) => this with { Expenses = Expenses.Add(e.ExpenseId) };
}
