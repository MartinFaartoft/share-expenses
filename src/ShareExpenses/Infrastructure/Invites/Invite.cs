using Marten;
using Marten.Schema;
using ShareExpenses.Shared;

namespace ShareExpenses.Infrastructure.Invites;

[DocumentAlias("invite")]
internal sealed class Invite
{
    public Guid Id { get; set; }

    public GroupId GroupId { get; set; }

    public MemberId MemberId { get; set; }

    public string Email { get; set; } = "";

    public string NormalizedEmail { get; set; } = "";

    public InviteId InviteId => InviteId.From(Id);

    public static async Task<IReadOnlyList<Invite>> GetInvitesFor(IQuerySession session, string email, GroupId? group, CancellationToken ct)
    {
        var key = EmailAddress.Normalize(email);
        if (key.Length == 0)
            return [];

        var query = session.Query<Invite>().Where(i => i.NormalizedEmail == key);
        if (group is { } g)
            query = query.Where(i => i.GroupId == g);
        return await query.ToListAsync(ct);
    }

    public static void Register(StoreOptions opts) =>
        opts.Schema.For<Invite>().Index(i => i.NormalizedEmail);
}
