using Microsoft.Extensions.DependencyInjection.Extensions;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

public static class FileRegistration
{
    /// <summary>Registers the D1 file slice. Call from Program.cs after MongoStore.</summary>
    public static IServiceCollection AddFileServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ProjectLabelLock>();
        services.TryAddScoped<LabelRequestContext>();
        services.AddSingleton<FileMutationGate>();
        services.AddScoped<FileService>();
        return services;
    }

    public static WebApplication MapFileEndpoints(this WebApplication app)
    {
        FileEndpoints.MapFiles(app);
        return app;
    }

    /// <summary>
    /// Program.cs wiring (not done in this slice):
    ///   builder.Services.AddFileServices();
    ///   app.MapFileEndpoints();
    ///   await app.Services.GetRequiredService&lt;FileService&gt;().InitializeAsync();
    /// </summary>
    public static Task InitializeFileIndexesAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<FileService>().InitializeAsync(ct);
    }
}
