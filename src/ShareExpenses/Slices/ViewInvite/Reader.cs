using ShareExpenses.Shared;

namespace ShareExpenses.Slices.ViewInvite;

/// <summary>The query, as in <c>event-model.yaml</c>: group from the route, token from the link's fragment.</summary>
/// <param name="Now">The clock, passed in so reading stays pure and testable.</param>
internal sealed record Query(GroupId GroupId, string? Token, DateTimeOffset Now);

/// <summary>The read model <c>InviteLookup</c>: what the landing page shows.</summary>
internal sealed record InviteLookup(string GroupName, string MemberName, string InvitedBy);

/// <summary>Specs: <c>docs/event-model/slice-04-view-invite.md</c>.</summary>
internal static class Reader
{
    /// <summary>
    /// The live invite the token belongs to, or null. Wrong token, unknown group,
    /// expired, superseded or used all read as null: the caller says "not found".
    /// </summary>
    /// <param name="state">The group's state, or null if its stream does not exist.</param>
    public static InviteLookup? Read(State? state, Query query)
    {
        if (state is null || string.IsNullOrEmpty(query.Token))
            return null;

        // Checks every open invite rather than stopping at the first match, so the
        // time taken does not depend on which slot (if any) the token belongs to.
        var found = default(KeyValuePair<MemberId, OpenInvite>?);
        foreach (var open in state.OpenInvites)
            if (InviteToken.Matches(query.Token, open.Value.TokenHash))
                found = open;

        if (found is not { } invite || query.Now >= invite.Value.ExpiresAt)
            return null;

        return new InviteLookup(
            state.GroupName,
            state.SlotNames[invite.Key],
            state.SlotNames[invite.Value.InvitedBy]);
    }
}
