using ShareExpenses.Shared;

namespace ShareExpenses.Slices.ViewInvites;

/// <summary>The query, as in <c>event-model.yaml</c>.</summary>
/// <param name="UserId">The signed-in user.</param>
/// <param name="Invites">
/// Looked up: the <c>Invite</c> documents addressed to the user's account email, as
/// group and invite. They propose; each group's stream decides (spec §11).
/// </param>
/// <param name="Now">The clock, passed in so reading stays pure and testable.</param>
internal sealed record Query(UserId UserId, IReadOnlyList<(GroupId Group, InviteId Invite)> Invites, DateTimeOffset Now);

/// <summary>
/// The read model, as in <c>event-model.yaml</c>: exactly what the Your invites screen
/// receives — the user's live invites, soonest deadline first.
/// </summary>
internal sealed record PendingInvitesReadModel(IReadOnlyList<PendingInvite> Invites);

internal sealed record PendingInvite(GroupId GroupId, string GroupName, string MemberName, string InvitedBy);

/// <summary>Specs: <c>docs/event-model/slice-04-view-invites.md</c>.</summary>
internal static class Reader
{
    /// <param name="groups">Each invited group's state, or null if its stream does not exist.</param>
    public static PendingInvitesReadModel Read(Query query, IReadOnlyDictionary<GroupId, State?> groups)
    {
        var live =
            from proposed in query.Invites
            let state = groups.GetValueOrDefault(proposed.Group)
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

        return new PendingInvitesReadModel(live.ToList());
    }
}
