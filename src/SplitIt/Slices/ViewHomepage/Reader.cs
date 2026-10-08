using SplitIt.Shared;

namespace SplitIt.Slices.ViewHomepage;

internal sealed record Query(UserId UserId, IReadOnlyList<(GroupId Group, InviteId Invite)> PendingInvites, DateTimeOffset Now);

/// <param name="Groups">The groups the user is in, by name.</param>
/// <param name="Archived">The archived ones, kept apart from the main list (spec §8).</param>
public sealed record HomepageReadModel(
    IReadOnlyList<PendingInvite> Invites, IReadOnlyList<GroupSummary> Groups, IReadOnlyList<GroupSummary> Archived);

public sealed record PendingInvite(GroupId GroupId, string GroupName, string MemberName, string InvitedBy);

public sealed record GroupSummary(GroupId GroupId, string Name);

internal static class Reader
{
    public static HomepageReadModel Read(Query query, IReadOnlyDictionary<GroupId, State?> invited, IReadOnlyList<Membership> memberships)
    {
        var invites =
            from proposed in query.PendingInvites
            let state = invited.GetValueOrDefault(proposed.Group)
            where state is not null && !state.Archived && !state.Members.ContainsKey(query.UserId)
            from open in state.OpenInvites
            where open.Value.InviteId == proposed.Invite && query.Now < open.Value.ExpiresAt
            orderby open.Value.ExpiresAt, state.GroupName
            select new PendingInvite(
                proposed.Group,
                state.GroupName,
                state.SlotNames[open.Key],
                state.SlotNames[open.Value.InvitedBy]);

        var mine = memberships
            .Where(m => m.Members.Contains(query.UserId))
            .OrderBy(m => m.GroupName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(m => m.Id)
            .ToList();
        GroupSummary Summary(Membership m) => new(GroupId.From(m.Id), m.GroupName);

        return new HomepageReadModel(
            invites.ToList(), mine.Where(m => !m.Archived).Select(Summary).ToList(), mine.Where(m => m.Archived).Select(Summary).ToList());
    }
}
