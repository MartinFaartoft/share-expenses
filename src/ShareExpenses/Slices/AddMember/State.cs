using System.Collections.Immutable;
using Marten.Schema;
using ShareExpenses.Shared;
using ShareExpenses.Slices.CreateGroup;

namespace ShareExpenses.Slices.AddMember;

/// <summary>
/// What AddMember needs to know about a group, folded from its stream by Marten
/// (<c>FetchForWriting</c>). No stream means no state: the group does not exist.
///
/// FOLD CHECKLIST — this state is private to the slice (spec §12), so nothing
/// forces it to keep up with new events. When these slices are built, fold their
/// events here and add the deferred specs in slice-02-add-member.md:
///   MemberClaimReleased → remove from Members        (a released claim ends membership)
///   MemberRemoved       → remove from NameKeys       (a removed member's name is reusable)
///   MemberRenamed       → swap the key in NameKeys   (old name free, new name taken)
///   GroupArchived / GroupUnarchived → track Archived (no changes to an archived group)
///
/// Internal until the slice has a screen: Wolverine will fetch it for the endpoint,
/// putting it in the endpoint's signature, so public (spec §12).
///
/// The alias is required: Marten names a type by its bare class name, and every
/// slice has a <c>State</c> — without it, two slices collide on <c>ledger.state</c>
/// and whichever is used second fails at runtime.
/// </summary>
[DocumentAlias("add_member_state")]
internal sealed record State(ImmutableHashSet<UserId> Members, ImmutableHashSet<string> NameKeys)
{
    /// <summary>The stream id, set by Marten; Wolverine needs it to type the aggregate's identity.</summary>
    public GroupId Id { get; init; }

    public static State Create(GroupCreated _) => new([], []);

    public State Apply(MemberAdded e) => this with { NameKeys = NameKeys.Add(Names.ComparisonKey(e.DisplayName)) };

    public State Apply(MemberClaimed e) => this with { Members = Members.Add(e.UserId) };
}
