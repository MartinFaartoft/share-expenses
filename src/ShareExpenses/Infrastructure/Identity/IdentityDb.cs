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
public sealed class IdentityDb(DbContextOptions<IdentityDb> options)
    : IdentityDbContext<IdentityUser>(options), IDataProtectionKeyContext
{
    public const string Schema = "identity";

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
    }
}
