using Marten;
using Marten.Schema;
using ShareExpenses.Shared;

namespace ShareExpenses.Infrastructure.Invites;

/// <summary>
/// An invite: binds a member slot to the email address it was invited at. A plain
/// document, not event-sourced (spec §3), so the address can be corrected or
/// erased; the ledger records only that the slot was invited, by whom, until when —
/// <c>MemberInvited</c>, whose <c>inviteId</c> is this document's id.
///
/// One per slot. InviteMember writes it in the same session as <c>MemberInvited</c>,
/// replacing any earlier one for the slot; AcceptInvite deletes it in the same session
/// as <c>MemberClaimed</c>. Lives in Infrastructure, not in a slice, because three
/// slices use it: InviteMember, ViewHomepage and AcceptInvite.
///
/// A lookup, so possibly stale: it answers "which invites were sent to this
/// address", and the group's stream decides whether each is still its slot's
/// current, open invite (spec §11).
/// </summary>
[DocumentAlias("invite")]
internal sealed class Invite
{
    /// <summary>The invite's id, <see cref="InviteId"/>'s value: what <c>MemberInvited.inviteId</c> names.</summary>
    public Guid Id { get; set; }

    public GroupId GroupId { get; set; }

    public MemberId MemberId { get; set; }

    /// <summary>As the inviter typed it, trimmed: what the email is sent to.</summary>
    public string Email { get; set; } = "";

    /// <summary>For matching: <see cref="EmailAddress.Normalize"/>, the same way Identity matches account emails.</summary>
    public string NormalizedEmail { get; set; } = "";

    public InviteId InviteId => InviteId.From(Id);

    /// <summary>The invites addressed to <paramref name="email"/>, in one group or in all of them.</summary>
    public static async Task<IReadOnlyList<Invite>> AddressedTo(
        IQuerySession session, string email, GroupId? group, CancellationToken ct)
    {
        var key = EmailAddress.Normalize(email);
        if (key.Length == 0)
            return [];

        var query = session.Query<Invite>().Where(i => i.NormalizedEmail == key);
        if (group is { } g)
            query = query.Where(i => i.GroupId == g);
        return await query.ToListAsync(ct);
    }

    /// <summary>Registers the document and the index the address lookup uses.</summary>
    public static void Register(StoreOptions opts) =>
        opts.Schema.For<Invite>().Index(i => i.NormalizedEmail);
}
