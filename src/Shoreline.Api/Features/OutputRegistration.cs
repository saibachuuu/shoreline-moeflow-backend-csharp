using Microsoft.Extensions.DependencyInjection.Extensions;
using Shoreline.Api.Jobs;

namespace Shoreline.Api.Features;

public static class OutputRegistration
{
    /// <summary>Standalone registration; Program/startup wiring is owned by the integrator.</summary>
    public static IServiceCollection AddOutputServices(this IServiceCollection services)
    {
        services.AddScoped<OutputService>();
        services.AddScoped<OutputExportService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, OutputJobHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, TeamOutputJobHandler>());
        return services;
    }


}
