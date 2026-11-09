using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Modules.StoragePool;

public static class StoragePoolRegistration
{
    /// <summary>
    /// Registers the optional module. Program.cs is intentionally not modified by
    /// this phase; the host must call this method explicitly.
    /// </summary>
    public static IServiceCollection AddStoragePoolServices(this IServiceCollection services)
    {
        services.AddProjectStorageCore();
        services.TryAddSingleton<ProjectLabelLock>();
        services.AddSingleton<StoragePoolSourceResolver>();
        services.AddScoped<StoragePoolSourceService>();
        services.AddScoped<StoragePoolSettingsService>();
        services.AddScoped<StoragePoolLockService>();
        services.AddScoped<StoragePoolTransferEngine>();
        services.AddScoped<StoragePoolTransferService>();
        services.AddScoped<StoragePoolProjectService>();
        services.AddScoped<StoragePoolScanService>();
        services.AddHostedService<StoragePoolWorker>();
        return services;
    }

    /// <summary>
    /// Called once from the host's startup after MongoDB is initialized. It
    /// registers the module resolver and creates only this module's indexes.
    /// </summary>
    public static async Task InitializeStoragePoolAsync(this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ProjectStorageResolverRegistry>();
        registry.Register(scope.ServiceProvider.GetRequiredService<StoragePoolSourceResolver>());

        var store = scope.ServiceProvider.GetRequiredService<MongoStore>();
        await store.Collection(StoragePoolConstants.SourceCollection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("e"),
                new CreateIndexOptions { Name = "storage_pool_source_enabled" }),
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await store.Collection(StoragePoolConstants.TransferCollection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("p"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "storage_transfer_one_active_project",
                    Unique = true,
                    PartialFilterExpression =
                        Builders<BsonDocument>.Filter.Gte("st", 0) &
                        Builders<BsonDocument>.Filter.Lt("st", 4),
                }),
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await store.Collection(StoragePoolConstants.TransferCollection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("p").Ascending("s"),
                new CreateIndexOptions { Name = "storage_transfer_project_source" }),
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await store.Collection(StoragePoolConstants.TransferCollection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("st"),
                new CreateIndexOptions { Name = "storage_transfer_status" }),
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await store.Collection(StoragePoolConstants.TransferCollection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Descending("ct").Descending("_id"),
                new CreateIndexOptions { Name = "storage_transfer_create_time" }),
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await store.Collection(StoragePoolConstants.ObjectCollection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("t").Ascending("st"),
                new CreateIndexOptions { Name = "storage_transfer_object_status" }),
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await store.Collection(StoragePoolConstants.ObjectCollection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("sk"),
                new CreateIndexOptions { Name = "storage_transfer_object_key" }),
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await store.Collection("project").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("csid"),
                new CreateIndexOptions { Name = "project_storage_source" }),
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var settings = scope.ServiceProvider.GetRequiredService<StoragePoolSettingsService>();
        await settings.ReadAsync(cancellationToken).ConfigureAwait(false);
    }
}
