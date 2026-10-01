using ShareExpenses.Shared;

namespace ShareExpenses.Slices.InviteMember;

/// <summary>The command, as in <c>event-model.yaml</c>. Inputs are raw; deciding validates them.</summary>
/// <param name="TokenHash">Hash of the token generated for this invite; see <see cref="InviteToken"/>.</param>
/// <param name="EmailHolder">
/// Looked up: the user whose account email is <paramref name="Email"/>, if any.
/// </param>
/// <param name="InvitedTo">
/// Looked up: slots in this group whose latest invite delivery went to <paramref name="Email"/>.
/// </param>
/// <remarks>
/// The two lookups feed <em>guards</em>, not invariants (spec §11): they come from
/// outside the stream and may be stale. The worst a stale lookup can do is let a
/// mistake through to claim time, where "one user, one slot" is enforced against
/// the stream itself.
/// </remarks>
internal sealed record Command(
    GroupId GroupId,
    MemberId MemberId,
    string? Email,
    string TokenHash,
    UserId By,
    UserId? EmailHolder = null,
    IReadOnlySet<MemberId>? InvitedTo = null);

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

        var email = NormaliseEmail(command.Email);
        if (!IsPlausibleEmail(email))
            return Decision.Reject("email is not a valid address");

        // Guard: the address belongs to someone already in the group — including
        // the creator, and whatever address they use today.
        if (command.EmailHolder is { } holder && state.Members.TryGetValue(holder, out var held))
            return Decision.Reject($"{email} has already joined as {state.Slots[held].Name}");

        // Guard: the address has an open invite on another slot. Deliveries to slots
        // since claimed (or gone) no longer hold the address.
        var openElsewhere = (command.InvitedTo ?? new HashSet<MemberId>())
            .Where(m => m != command.MemberId)
            .Select(m => state.Slots.GetValueOrDefault(m))
            .FirstOrDefault(s => s is { Claimed: false });
        if (openElsewhere is not null)
            return Decision.Reject($"that email is already invited as {openElsewhere.Name}");

        return Decision.Accept(new MemberInvited(command.MemberId, command.TokenHash, command.By));
    }

    /// <summary>Trimmed; case kept — the local part is technically case-sensitive.</summary>
    public static string NormaliseEmail(string? email) => email?.Trim() ?? "";

    /// <summary>
    /// Plausibly an address, nothing more: real validation is whether mail arrives.
    /// At most 254 characters, exactly one @ with something on both sides, no whitespace.
    /// </summary>
    private static bool IsPlausibleEmail(string email) =>
        email.Length is > 0 and <= MaxEmailLength
        && !email.Any(char.IsWhiteSpace)
        && email.Split('@') is [{ Length: > 0 }, { Length: > 0 }];
}
