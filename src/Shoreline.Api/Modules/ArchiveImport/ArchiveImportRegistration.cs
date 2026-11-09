using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;

namespace Shoreline.Api.Modules.ArchiveImport;

public static class ArchiveImportRegistration
{
    public static IServiceCollection AddArchiveImportServices(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => ArchiveImportOptions.From(sp.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton<ArchiveImportTransport>();
        services.TryAddSingleton<IArchiveImportTransport>(sp => sp.GetRequiredService<ArchiveImportTransport>());
        services.TryAddSingleton<IArchiveProjectUploader, FileServiceArchiveUploader>();
        services.TryAddSingleton<IArchiveJobQueue, ArchiveJobQueue>();
        services.TryAddScoped<ArchiveImportTaskStore>();
        services.TryAddScoped<ArchiveImportClaimStore>();
        services.TryAddScoped<ArchiveImportService>();
        services.TryAddSingleton<IJobHandler, ArchiveImportJobHandler>();
        AddFileServicesIfPresent(services);
        return services;
    }

    public static async Task InitializeArchiveImportIndexesAsync(this IServiceProvider services,
        CancellationToken ct = default)
    {
        var store = services.GetRequiredService<MongoStore>();
        await store.Collection("archive_import_task").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("p").Descending("_id"),
                new CreateIndexOptions { Name = "archive_import_task_project_latest" }), cancellationToken: ct);
        await store.Collection("archive_import_task").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("p").Ascending("s"),
                new CreateIndexOptions { Name = "archive_import_task_project_status" }), cancellationToken: ct);
        await store.Collection("archive_import_active").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("task_id"),
                new CreateIndexOptions { Name = "archive_import_active_task" }), cancellationToken: ct);
    }

    private static void AddFileServicesIfPresent(IServiceCollection services)
    {
        var type = Type.GetType("Shoreline.Api.Features.FileRegistration, Shoreline.Api");
        if (type is null) return;
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(candidate => candidate.Name == "AddFileServices"
                && candidate.GetParameters() is [var parameter]
                && parameter.ParameterType == typeof(IServiceCollection));
        method?.Invoke(null, [services]);
    }
}
