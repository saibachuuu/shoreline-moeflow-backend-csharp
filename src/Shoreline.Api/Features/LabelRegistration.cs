using Microsoft.Extensions.DependencyInjection;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

/// <summary>
/// DI and startup hooks for the E1 label chain. The checkpoint Program calls:
///
///   builder.Services.AddLabelServices();
///   app.UseLabelRequestGuard();          // inside the ApiError middleware
///   app.MapLabels();
///   await app.Services.InitializeLabelIndexesAsync();
///
/// The guard must run after routing (it reads RouteValues) and before the
/// label endpoints. All label reads take the shared project lease too.
/// </summary>
public static class LabelRegistration
{
    public static IServiceCollection AddLabelServices(this IServiceCollection services)
    {
        services.AddSingleton<ProjectLabelLock>();
        services.AddSingleton<LabelRequestGuardHooks>();
        services.AddScoped<LabelRequestContext>();
        services.AddScoped<LabelService>();
        return services;
    }

    /// <summary>
    /// Creates the source/translation/tip/file_target_cache indexes. Safe to call
    /// on every startup; the legacy unique indexes are accepted by key spec.
    /// </summary>
    public static async Task InitializeLabelIndexesAsync(
        this IServiceProvider services,
        CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var labels = scope.ServiceProvider.GetRequiredService<LabelService>();
        await labels.InitializeAsync(ct);
    }
}
