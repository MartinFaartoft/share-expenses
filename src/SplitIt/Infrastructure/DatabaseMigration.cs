using Marten;
using Microsoft.EntityFrameworkCore;
using SplitIt.Infrastructure.Identity;

namespace SplitIt.Infrastructure;

/// <summary>
/// Brings every store up to date before the app serves anything, in every environment
/// (spec §3): the VPS runs no migration hook. A failure stops the process.
/// </summary>
public static class DatabaseMigration
{
    public static async Task MigrateDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityDb>().Database.MigrateAsync();
        await app.Services.GetRequiredService<IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    }
}
