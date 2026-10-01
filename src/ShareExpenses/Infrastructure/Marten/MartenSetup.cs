using JasperFx;
using Marten;

namespace ShareExpenses.Infrastructure.Marten;

/// <summary>Event store and projected documents, in their own <c>ledger</c> schema (spec §3, §11).</summary>
public static class MartenSetup
{
    public const string Schema = "ledger";

    public static IServiceCollection AddLedgerStore(
        this IServiceCollection services, string connectionString, IHostEnvironment env)
    {
        services
            .AddMarten(opts =>
            {
                opts.Connection(connectionString);
                opts.DatabaseSchemaName = Schema;
                opts.Events.DatabaseSchemaName = Schema;

                // Development creates/patches schema objects on the fly. Anywhere else the
                // schema must already exist — apply changes deliberately, not at runtime.
                opts.AutoCreateSchemaObjects = env.IsDevelopment()
                    ? AutoCreate.CreateOrUpdate
                    : AutoCreate.None;

                // Event types and projections belong to the slices that own them.
                AllSlices.Register(opts);
            })
            .UseLightweightSessions();

        return services;
    }
}
