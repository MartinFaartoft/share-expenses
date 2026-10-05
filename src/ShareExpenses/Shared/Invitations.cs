namespace ShareExpenses.Shared;

/// <summary>
/// The rules for inviting an address to a member slot, one function for every slice
/// that invites (AddMember with an email, InviteMember) so they cannot drift apart.
/// </summary>
internal static class Invitations
{
    /// <summary>
    /// How long a new invite lives. Applied once, when inviting, and recorded on the
    /// event as a deadline: changing this does not move invites already sent.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>Why <paramref name="email"/> may not be invited, or null if it may.</summary>
    /// <param name="email">Trimmed.</param>
    /// <param name="emailHolder">Looked up: the account with this address, if any.</param>
    /// <param name="invitedTo">Looked up: the slots whose <c>Invite</c> is addressed to it.</param>
    /// <param name="except">The slot being invited: an open invite there is being replaced, not in the way.</param>
    /// <param name="joinedAs">The name of the slot a user holds in this group, if any.</param>
    /// <param name="openSlotName">The name of a slot that exists and is unclaimed, else null.</param>
    public static string? Refusal(
        string email,
        UserId? emailHolder,
        IEnumerable<MemberId>? invitedTo,
        MemberId except,
        Func<UserId, string?> joinedAs,
        Func<MemberId, string?> openSlotName)
    {
        if (!EmailAddress.IsPlausible(email))
            return "email is not a valid address";

        // Someone already in the group, including the creator, whatever address they use today.
        if (emailHolder is { } holder && joinedAs(holder) is { } joined)
            return $"{email} has already joined as {joined}";

        // An open invite on another slot; invites to slots since claimed (or gone) hold nothing.
        var elsewhere = (invitedTo ?? []).Where(m => m != except).Select(openSlotName).FirstOrDefault(n => n is not null);
        return elsewhere is null ? null : $"that email is already invited as {elsewhere}";
    }
}
