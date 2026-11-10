using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;

namespace Shoreline.Api.Features;

public static class FileParseRegistration
{
    /// <summary>
    /// Registers the two text-parse celery-task equivalents. Call after
    /// <c>AddFileServices()</c> (or rely on the TryAdd lock/context fallbacks).
    /// No new index is required: the task reuses the existing file/source
    /// indexes created by <c>InitializeFileIndexesAsync</c>.
    /// </summary>
    public static IServiceCollection AddFileParseServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ProjectLabelLock>();
        services.TryAddScoped<LabelRequestContext>();
        services.TryAddScoped<FileParseService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, ParseTextJobHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, FindTermsJobHandler>());
        return services;
    }
}
