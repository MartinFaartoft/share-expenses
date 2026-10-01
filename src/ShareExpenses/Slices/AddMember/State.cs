using System.Collections.Immutable;
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
/// Convention methods must be public for Marten's source generator; the type is
/// internal, so they are not visible outside the assembly.
/// </summary>
internal sealed record State(ImmutableHashSet<UserId> Members, ImmutableHashSet<string> NameKeys)
{
    public static State Create(GroupCreated _) => new([], []);

    public State Apply(MemberAdded e) => this with { NameKeys = NameKeys.Add(Names.ComparisonKey(e.DisplayName)) };

    public State Apply(MemberClaimed e) => this with { Members = Members.Add(e.UserId) };
}
