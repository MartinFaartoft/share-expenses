using ShareExpenses.Shared;

namespace ShareExpenses.Slices.ViewInvite;

/// <summary>
/// The query, as in <c>event-model.yaml</c>: the token from the link's fragment. The group
/// from the link only selects the stream, so it is not here (spec §13).
/// </summary>
/// <param name="Now">The clock, passed in so reading stays pure and testable.</param>
internal sealed record Query(string? Token, DateTimeOffset Now);

/// <summary>
/// The read model, as in <c>event-model.yaml</c>: exactly what the landing page
/// receives. Selected from the slice's <see cref="State"/> by <see cref="Reader"/>.
/// </summary>
internal sealed record InviteReadModel(string GroupName, string MemberName, string InvitedBy);

/// <summary>Specs: <c>docs/event-model/slice-04-view-invite.md</c>.</summary>
internal static class Reader
{
    /// <summary>
    /// The live invite the token belongs to, or null. Wrong token, unknown group,
    /// expired, superseded or used all read as null: the caller says "not found".
    /// </summary>
    /// <param name="state">The group's state, or null if its stream does not exist.</param>
    public static InviteReadModel? Read(State? state, Query query)
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

        return new InviteReadModel(
            state.GroupName,
            state.SlotNames[invite.Key],
            state.SlotNames[invite.Value.InvitedBy]);
    }
}
