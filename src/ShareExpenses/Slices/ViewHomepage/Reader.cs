using ShareExpenses.Shared;

namespace ShareExpenses.Slices.ViewHomepage;

/// <summary>The query, as in <c>event-model.yaml</c>.</summary>
/// <param name="UserId">The signed-in user.</param>
/// <param name="InvitedAs">
/// Looked up: the <c>Invite</c> documents addressed to the user's account email, as
/// group and invite. They propose; each group's stream decides (spec §11).
/// </param>
/// <param name="Now">The clock, passed in so reading stays pure and testable.</param>
internal sealed record Query(UserId UserId, IReadOnlyList<(GroupId Group, InviteId Invite)> InvitedAs, DateTimeOffset Now);

/// <summary>
/// The read model, as in <c>event-model.yaml</c>: exactly what the Home screen
/// receives — invites waiting, soonest deadline first, then the user's groups by name.
/// Public, with its parts, because the screen takes it as a component parameter, and
/// component parameters must be public (spec §3).
/// </summary>
public sealed record HomepageReadModel(IReadOnlyList<PendingInvite> Invites, IReadOnlyList<GroupSummary> Groups);

public sealed record PendingInvite(GroupId GroupId, string GroupName, string MemberName, string InvitedBy);

public sealed record GroupSummary(GroupId GroupId, string Name);

/// <summary>Specs: <c>docs/event-model/slice-04-view-homepage.md</c>.</summary>
internal static class Reader
{
    /// <param name="invited">Each invited group's live state, or null if its stream does not exist.</param>
    /// <param name="memberships">The <c>UserGroups</c> documents that list the user.</param>
    public static HomepageReadModel Read(
        Query query, IReadOnlyDictionary<GroupId, State?> invited, IReadOnlyList<Membership> memberships)
    {
        var invites =
            from proposed in query.InvitedAs
            let state = invited.GetValueOrDefault(proposed.Group)
            // A group the user is already in: they cannot claim a second slot in it.
            where state is not null && !state.Members.ContainsKey(query.UserId)
            // The stream decides: the proposed invite must be its slot's current one.
            from open in state.OpenInvites
            where open.Value.InviteId == proposed.Invite && query.Now < open.Value.ExpiresAt
            orderby open.Value.ExpiresAt, state.GroupName
            select new PendingInvite(
                proposed.Group,
                state.GroupName,
                state.SlotNames[open.Key],
                state.SlotNames[open.Value.InvitedBy]);

        var groups = memberships
            .Where(m => m.Members.Contains(query.UserId))
            .OrderBy(m => m.GroupName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(m => m.Id)
            .Select(m => new GroupSummary(GroupId.From(m.Id), m.GroupName));

        return new HomepageReadModel(invites.ToList(), groups.ToList());
    }
}
