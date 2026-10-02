using ShareExpenses.Infrastructure.Identity;
using Wolverine;
using Wolverine.Http;

namespace ShareExpenses.Infrastructure.Wolverine;

/// <summary>
/// Wolverine (spec §12, phase 2): every slice's endpoint runs on its HTTP endpoints
/// and aggregate handler workflow. Marten's side of the integration is
/// <c>IntegrateWithWolverine</c> in <c>MartenSetup</c>.
/// </summary>
public static class WolverineSetup
{
    public const string Schema = "wolverine";

    public static void AddWolverineEndpoints(this WebApplicationBuilder builder)
    {
        builder.Host.UseWolverine(opts =>
        {
            // Mediator only: no messaging, no inbox/outbox agents — HTTP endpoints and
            // the aggregate handler workflow are all that is used. The durable outbox
            // (spec §14, emails) would mean moving off this mode.
            opts.Durability.Mode = DurabilityMode.MediatorOnly;

            // Wolverine builds services inline in generated code, so it needs their
            // concrete types public. The Identity-backed directory stays internal;
            // resolving it from the container instead is the allowed exception.
            opts.CodeGeneration.AlwaysUseServiceLocationFor<IEmailDirectory>();
        });
        builder.Services.AddWolverineHttp();
    }

    /// <summary>
    /// Wolverine finds its endpoints by scanning the assembly — there is no explicit
    /// registration to call. <c>dotnet run -- describe</c> lists what it found.
    /// </summary>
    public static void MapWolverineEndpoints(this WebApplication app) =>
        app.MapWolverineEndpoints(opts => opts.RoutePrefix("api"));
}
