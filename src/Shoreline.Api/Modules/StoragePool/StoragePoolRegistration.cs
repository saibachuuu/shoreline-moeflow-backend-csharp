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
        await EnsureNoDuplicateActiveTransfersAsync(store, cancellationToken).ConfigureAwait(false);
        await store.EnsureIndexTolerantAsync(StoragePoolConstants.SourceCollection, new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("e"),
                new CreateIndexOptions { Name = "storage_pool_source_enabled" }), cancellationToken).ConfigureAwait(false);
        await store.EnsureIndexTolerantAsync(StoragePoolConstants.TransferCollection, new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("p"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "storage_transfer_one_active_project",
                    Unique = true,
                    PartialFilterExpression =
                        Builders<BsonDocument>.Filter.Gte("st", 0) &
                        Builders<BsonDocument>.Filter.Lt("st", 4),
                }), cancellationToken).ConfigureAwait(false);
        await store.EnsureIndexTolerantAsync(StoragePoolConstants.TransferCollection, new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("p").Ascending("s"),
                new CreateIndexOptions { Name = "storage_transfer_project_source" }), cancellationToken).ConfigureAwait(false);
        await store.EnsureIndexTolerantAsync(StoragePoolConstants.TransferCollection, new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("st"),
                new CreateIndexOptions { Name = "storage_transfer_status" }), cancellationToken).ConfigureAwait(false);
        await store.EnsureIndexTolerantAsync(StoragePoolConstants.TransferCollection, new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Descending("ct").Descending("_id"),
                new CreateIndexOptions { Name = "storage_transfer_create_time" }), cancellationToken).ConfigureAwait(false);
        await store.EnsureIndexTolerantAsync(StoragePoolConstants.ObjectCollection, new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("t").Ascending("st"),
                new CreateIndexOptions { Name = "storage_transfer_object_status" }), cancellationToken).ConfigureAwait(false);
        await store.EnsureIndexTolerantAsync(StoragePoolConstants.ObjectCollection, new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("sk"),
                new CreateIndexOptions { Name = "storage_transfer_object_key" }), cancellationToken).ConfigureAwait(false);
        await store.EnsureIndexTolerantAsync("project", new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("csid"),
                new CreateIndexOptions { Name = "project_storage_source" }), cancellationToken).ConfigureAwait(false);

        var settings = scope.ServiceProvider.GetRequiredService<StoragePoolSettingsService>();
        await settings.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pre-flight dirty-data check required before creating the unique partial
    /// index on active transfers. Historical rows may contain more than one
    /// active transfer for the same project; creating the index would then fail
    /// with a duplicate-key error. Fail startup with a safe message instead and
    /// let the operator resolve the rows first.
    /// </summary>
    internal static async Task EnsureNoDuplicateActiveTransfersAsync(MongoStore store,
        CancellationToken cancellationToken)
    {
        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                ["st"] = new BsonDocument { ["$gte"] = 0, ["$lt"] = 4 },
            }),
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = "$p",
                ["count"] = new BsonDocument("$sum", 1),
            }),
            new BsonDocument("$match", new BsonDocument("count", new BsonDocument("$gt", 1))),
            new BsonDocument("$limit", 20),
        });
        var duplicates = await store.Collection(StoragePoolConstants.TransferCollection)
            .Aggregate(pipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "storage_transfer has projects with more than one active transfer; " +
                "clean up those rows before creating storage_transfer_one_active_project.");
        }
    }
}
