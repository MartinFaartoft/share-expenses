using ShareExpenses.Infrastructure.Identity;
using Wolverine;
using Wolverine.Http;

namespace ShareExpenses.Infrastructure.Wolverine;

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
            opts.CodeGeneration.AlwaysUseServiceLocationFor<IEmailDirectory>();
            opts.CodeGeneration.AlwaysUseServiceLocationFor<IEmailSender>();
        });
        builder.Services.AddWolverineHttp();
    }

    public static void MapWolverineEndpoints(this WebApplication app) =>
        WolverineHttpEndpointRouteBuilderExtensions.MapWolverineEndpoints(app);
}
