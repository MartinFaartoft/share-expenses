using JasperFx;
using JasperFx.Events.Daemon;
using Marten;
using ShareExpenses.Infrastructure.Invites;
using ShareExpenses.Infrastructure.Wolverine;
using ShareExpenses.Shared;
using Wolverine.Marten;

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

                // Typed ids are bare Guids in JSON; registering them lets LINQ
                // compare and search them like Guids (spec §12, verified by spike).
                opts.RegisterValueType(typeof(GroupId));
                opts.RegisterValueType(typeof(MemberId));
                opts.RegisterValueType(typeof(UserId));
                opts.RegisterValueType(typeof(InviteId));
                opts.RegisterValueType(typeof(ExpenseId));
                opts.RegisterValueType(typeof(SettlementId));

                // Supporting state shared by slices (spec §3).
                Invite.Register(opts);

                // Event types and projections belong to the slices that own them.
                AllSlices.Register(opts);
            })
            .UseLightweightSessions()
            // Runs the asynchronous projections (spec §11) in this process: one server, so Solo.
            .AddAsyncDaemon(DaemonMode.Solo)
            // Lets Wolverine's aggregate handler workflow open sessions and commit them
            // (spec §12). Wolverine's own tables live in their own schema.
            .IntegrateWithWolverine(w => w.MessageStorageSchemaName = WolverineSetup.Schema);

        return services;
    }
}
