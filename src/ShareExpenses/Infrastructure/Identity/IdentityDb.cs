using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ShareExpenses.Infrastructure.Identity;

/// <summary>
/// EF Core, strictly confined to identity (spec §4): users, logins and the
/// data-protection key ring, in its own <c>identity</c> schema. Never joined to
/// domain data — Marten owns the event store and projections.
/// </summary>
/// <summary>
/// A login identity. Guid-keyed because user ids are recorded in domain events
/// (<c>createdBy</c>, <c>MemberClaimed.userId</c>) and must have a stable, typed shape.
/// </summary>
public sealed class User : IdentityUser<Guid>;

public sealed class IdentityDb(DbContextOptions<IdentityDb> options)
    : IdentityDbContext<User, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public const string Schema = "identity";

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
    }
}
