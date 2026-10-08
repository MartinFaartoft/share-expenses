using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;

namespace SplitIt.Slices.RenameGroup;

/// <summary>
/// What RenameGroup needs to know about a group: its name as it stands, to tell a rename
/// from no change, and who holds a slot, to know who is a member. The Rename group
/// screen is built from it as well as decided against.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-14-rename-group.md:
///   MemberClaimReleased → remove from Members        (a released claim ends membership)
///   GroupArchived       → track Archived             (an archived group cannot be renamed)
///
/// Public: Wolverine fetches it for the endpoint (spec §12). The alias is required:
/// every slice has a State.
/// </summary>
[DocumentAlias("rename_group_state")]
public sealed record State(string GroupName, ImmutableDictionary<UserId, MemberId> Members)
{
    /// <summary>The stream id, set by Marten; Wolverine needs it to type the aggregate's identity.</summary>
    public GroupId Id { get; init; }

    public static State Create(GroupCreated e) => new(e.Name, ImmutableDictionary<UserId, MemberId>.Empty);

    public State Apply(MemberClaimed e) => this with { Members = Members.SetItem(e.UserId, e.MemberId) };

    public State Apply(GroupRenamed e) => this with { GroupName = e.Name };
}
