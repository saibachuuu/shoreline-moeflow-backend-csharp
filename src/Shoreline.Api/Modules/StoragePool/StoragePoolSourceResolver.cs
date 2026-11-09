using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Modules.StoragePool;

/// <summary>
/// The only bridge from the module-neutral router to this module's source
/// collection. Disabled or missing sources resolve to null so the router fails
/// closed instead of using the default bucket.
/// </summary>
public sealed class StoragePoolSourceResolver : IProjectStorageResolver
{
    private readonly MongoStore store;
    private readonly StorageBackendFactory factory;

    public StoragePoolSourceResolver(MongoStore store, StorageBackendFactory factory)
    {
        this.store = store;
        this.factory = factory;
    }

    private IMongoCollection<BsonDocument> Sources => store.Collection(StoragePoolConstants.SourceCollection);

    public async Task<StorageBackendLease?> ResolveAsync(ObjectId sourceId, CancellationToken cancellationToken)
    {
        var row = await Sources.Find(new BsonDocument("_id", sourceId)).FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null || !StoragePoolBson.Bool(row, "e", true)) return null;
        try
        {
            return factory.CreateLease(StoragePoolSourceService.Descriptor(row));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public sealed class StoragePoolLockService
{
    private readonly MongoStore store;
    private readonly TimeProvider clock;

    public StoragePoolLockService(MongoStore store, TimeProvider clock)
    {
        this.store = store;
        this.clock = clock;
    }

    private IMongoCollection<BsonDocument> Locks => store.Collection(StoragePoolConstants.LockCollection);

    public async Task<bool> AcquireAsync(ObjectId projectId, ObjectId transferId, string owner,
        int ttlSeconds = StoragePoolConstants.LockTtlSeconds, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expireAt = now.AddSeconds(ttlSeconds);
        var takeover = await Locks.UpdateOneAsync(
            new BsonDocument { ["_id"] = projectId, ["ea"] = new BsonDocument("$lt", now) },
            new BsonDocument("$set", new BsonDocument
            {
                ["t"] = transferId,
                ["o"] = owner,
                ["ea"] = expireAt,
            }), cancellationToken: ct).ConfigureAwait(false);
        if (takeover.ModifiedCount == 1) return true;
        try
        {
            await Locks.InsertOneAsync(new BsonDocument
            {
                ["_id"] = projectId,
                ["t"] = transferId,
                ["o"] = owner,
                ["ea"] = expireAt,
            }, cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (MongoWriteException error) when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
        catch
        {
            // The insert may have committed even though the driver reported an
            // unknown result. Remove only this owner's lock before surfacing.
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Locks.DeleteOneAsync(new BsonDocument
                {
                    ["_id"] = projectId,
                    ["t"] = transferId,
                    ["o"] = owner,
                }, timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // The lease expires on its own.
            }
            throw;
        }
    }

    public async Task<bool> RenewAsync(ObjectId projectId, ObjectId transferId, string owner,
        int ttlSeconds = StoragePoolConstants.LockTtlSeconds, CancellationToken ct = default)
    {
        var expireAt = clock.GetUtcNow().UtcDateTime.AddSeconds(ttlSeconds);
        var result = await Locks.UpdateOneAsync(
            new BsonDocument { ["_id"] = projectId, ["t"] = transferId, ["o"] = owner },
            new BsonDocument("$set", new BsonDocument("ea", expireAt)), cancellationToken: ct)
            .ConfigureAwait(false);
        return result.ModifiedCount == 1;
    }

    public Task ReleaseAsync(ObjectId projectId, ObjectId transferId, string owner,
        CancellationToken ct = default) =>
        Locks.DeleteOneAsync(new BsonDocument
        {
            ["_id"] = projectId,
            ["t"] = transferId,
            ["o"] = owner,
        }, ct);
}
