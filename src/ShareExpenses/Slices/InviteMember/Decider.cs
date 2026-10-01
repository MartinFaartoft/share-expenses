using ShareExpenses.Shared;

namespace ShareExpenses.Slices.InviteMember;

/// <summary>The command, as in <c>event-model.yaml</c>. Inputs are raw; deciding validates them.</summary>
/// <param name="TokenHash">Hash of the token generated for this invite; see <see cref="InviteToken"/>.</param>
internal sealed record Command(GroupId GroupId, MemberId MemberId, string? Email, string TokenHash, UserId By);

/// <summary>Specs: <c>docs/event-model/slice-03-invite-member.md</c>.</summary>
internal static class Decider
{
    /// <summary>
    /// The single answer for "no such group" and "not a member", so a non-member
    /// cannot probe which groups exist.
    /// </summary>
    public const string GroupNotFound = "group not found";

    public const string MemberNotFound = "member not found";

    private const int MaxEmailLength = 254;

    /// <param name="state">The group's state, or null if its stream does not exist.</param>
    public static Decision Decide(State? state, Command command)
    {
        // Membership first: a non-member learns nothing, not even from validation.
        if (state is null || !state.Members.ContainsKey(command.By))
            return Decision.Reject(GroupNotFound);

        if (!state.Slots.TryGetValue(command.MemberId, out var slot))
            return Decision.Reject(MemberNotFound);
        if (slot.Claimed)
            return Decision.Reject("member has already joined");

        var email = command.Email?.Trim() ?? "";
        if (!IsPlausibleEmail(email))
            return Decision.Reject("email is not a valid address");

        // One email, one slot: invited or already joined through an invite.
        var holder = state.Slots
            .Where(s => s.Key != command.MemberId)
            .Select(s => s.Value)
            .FirstOrDefault(s => string.Equals(s.Email, email, StringComparison.OrdinalIgnoreCase));
        if (holder is not null)
            return Decision.Reject(holder.Claimed
                ? $"{email} has already joined as {holder.Name}"
                : $"that email is already invited as {holder.Name}");

        return Decision.Accept(new MemberInvited(command.MemberId, email, command.TokenHash, command.By));
    }

    /// <summary>
    /// Plausibly an address, nothing more: real validation is whether mail arrives.
    /// At most 254 characters, exactly one @ with something on both sides, no whitespace.
    /// </summary>
    private static bool IsPlausibleEmail(string email) =>
        email.Length is > 0 and <= MaxEmailLength
        && !email.Any(char.IsWhiteSpace)
        && email.Split('@') is [{ Length: > 0 }, { Length: > 0 }];
}
