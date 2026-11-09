using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Imaging;

namespace Shoreline.Api.Features;

public static class ThumbnailRegistration
{
    public static void AddThumbnailServices(this IServiceCollection services)
    {
        // E1 normally registers these; TryAdd keeps the thumbnail assembly
        // usable in a minimal host without replacing E1's registrations.
        services.TryAddSingleton<ProjectLabelLock>();
        services.TryAddScoped<LabelRequestContext>();

        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            return new ImageProcessor(new ImageProcessingOptions
            {
                MaxSourceBytes = configuration.GetValue("Thumbnails:MaxSourceBytes", 64L * 1024 * 1024),
                MaxPixels = configuration.GetValue("Thumbnails:MaxPixels", 100_000_000L),
                MaxDimension = configuration.GetValue("Thumbnails:MaxDimension", 20_000),
                MaxDecodeMemoryBytes = (ulong)Math.Max(1, configuration.GetValue(
                    "Thumbnails:MaxDecodeMemoryBytes", 512L * 1024 * 1024)),
                MaxDecodeDiskBytes = (ulong)Math.Max(1, configuration.GetValue(
                    "Thumbnails:MaxDecodeDiskBytes", 1024L * 1024 * 1024))
            });
        });

        services.TryAddScoped<ThumbnailService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, ThumbnailJobHandler>());
    }

    public static void MapThumbnailEndpoints(this WebApplication app) => app.MapThumbnails();
}
