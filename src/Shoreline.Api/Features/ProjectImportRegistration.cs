using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;

namespace Shoreline.Api;

/// <summary>
/// Wiring extension for the LabelPlus import slice. Program.cs is owned by the
/// integration pass, so this file only exposes additive registrations:
///   builder.Services.AddProjectImports();
///   app.MapProjectImports();
/// </summary>
public static class ProjectImportRegistration
{
    public static IServiceCollection AddProjectImports(this IServiceCollection services)
    {
        // Additive only: use TryAdd so the label registration can own these
        // services when it is wired into Program.cs.
        services.TryAddSingleton<ProjectLabelLock>();
        services.TryAddScoped<LabelRequestContext>();
        services.TryAddScoped<LabelService>();
        services.AddScoped<ProjectImportService>();
        services.AddSingleton<IJobHandler, ProjectImportJobHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapProjectImports(this IEndpointRouteBuilder app)
    {
        app.MapProjectImportEndpoints();
        return app;
    }
}
