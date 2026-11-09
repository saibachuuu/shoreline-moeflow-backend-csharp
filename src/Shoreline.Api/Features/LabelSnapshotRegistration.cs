using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

/// <summary>
/// DI and route registration for the label snapshot feature. The main Program
/// must call <see cref="AddLabelSnapshots"/> and <see cref="MapLabelSnapshots"/>
/// after the shared label services and middleware are configured.
/// </summary>
public static class LabelSnapshotRegistration
{
    public static IServiceCollection AddLabelSnapshots(this IServiceCollection services)
    {
        // The middleware and the service must share the same scoped request
        // context so EnterAsync borrows the request lease instead of trying to
        // acquire a second Mongo lock (which would deadlock for ~2s).
        services.TryAddScoped<LabelRequestContext>();
        services.TryAddScoped<ProjectLabelLock>();
        services.AddScoped<LabelSnapshotService>();
        return services;
    }

    public static IEndpointRouteBuilder MapLabelSnapshots(this IEndpointRouteBuilder app)
    {
        return app.MapLabelSnapshotEndpoints();
    }

    public static async Task InitializeLabelSnapshotIndexesAsync(
        this IServiceProvider services,
        CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LabelSnapshotService>().InitializeAsync(ct);
    }
}
