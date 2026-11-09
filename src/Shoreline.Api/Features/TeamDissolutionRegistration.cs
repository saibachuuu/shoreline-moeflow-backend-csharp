using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

public static class TeamDissolutionRegistration
{
    public static IServiceCollection AddTeamDissolutionServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ProjectLabelLock>();
        services.TryAddScoped<LabelRequestContext>();
        services.TryAddSingleton<TeamLifecycleLock>();
        services.AddScoped<TeamDissolutionService>();
        return services;
    }

    public static WebApplication MapTeamDissolution(this WebApplication app) =>
        app.MapTeamDissolutionEndpoints();

    /// <summary>
    /// One startup pass for the durable lock, deleting marker, and project scan
    /// indexes. The shared Program calls this after the host is built, at the
    /// same point as MongoStore.InitializeAsync.
    /// </summary>
    public static async Task InitializeTeamDissolutionAsync(
        this IServiceProvider services,
        CancellationToken ct = default)
    {
        var lifecycle = services.GetRequiredService<TeamLifecycleLock>();
        await lifecycle.InitializeAsync(ct);
    }
}
