using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;

namespace SplitIt.Slices.ViewHomepage;

/// <summary>
/// The <c>UserGroups</c> projection's document: one per group, naming it and listing
/// the users who hold a slot in it. The home screen asks for those containing the
/// signed-in user (spec §11).
///
/// Stored, and projected <em>asynchronously</em> by Marten's daemon — deliberately,
/// to exercise that lifecycle; a moment's lag is harmless, because creating or
/// joining a group goes straight into it, never via home (spec §11).
///
/// One document per group rather than one per user: <c>MemberClaimed</c> carries no
/// group name, so a per-user document folded across streams would have to look the
/// name up when a claim arrives. Per group, the name is folded in order, and the
/// question "which groups is this user in" is a query, served by an index.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-04-view-homepage.md:
///   MemberClaimReleased → remove the user
///   GroupRenamed        → rename
///   GroupArchived / GroupUnarchived → track Archived (out of the main list, spec §8)
///
/// Changing the fold changes stored documents: rebuild with
/// <c>dotnet run -- projections rebuild</c>.
/// </summary>
[DocumentAlias("user_groups")]
internal sealed record Membership(string GroupName, UserId[] Members)
{
    /// <summary>The group's stream id: Marten keys the document by it.</summary>
    public Guid Id { get; init; }

    public static Membership Create(GroupCreated e) => new(e.Name, []);

    public Membership Apply(MemberClaimed e) => this with { Members = [.. Members, e.UserId] };
}
