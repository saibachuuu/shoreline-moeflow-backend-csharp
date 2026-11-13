using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Tests;

/// <summary>
/// A killed request cannot run its cleanup, and the lock row has no TTL, so the
/// next startup must be able to drop rows owned by a process that is gone.
/// </summary>
[Collection("local-services")]
public class ProjectLabelLockRecoveryTests(LocalServices services)
{
    [Fact]
    public async Task ClearStaleDropsLocksFromPreviousProcessesOnly()
    {
        var locks = new ProjectLabelLock(services.Store, TimeProvider.System);
        var collection = services.Store.Collection("project_label_lock");
        var staleId = ObjectId.GenerateNewId();
        var liveId = ObjectId.GenerateNewId();
        var started = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

        await collection.InsertManyAsync(
        [
            new BsonDocument
            {
                ["_id"] = staleId,
                ["owner"] = "previous-process",
                ["created"] = started.AddMinutes(-5),
            },
            new BsonDocument
            {
                ["_id"] = liveId,
                ["owner"] = "current-process",
                ["created"] = started.AddMinutes(5),
            },
        ]);

        var removed = await locks.ClearStaleAsync(started);

        Assert.Equal(1, removed);
        Assert.Equal(0, await collection.CountDocumentsAsync(new BsonDocument("_id", staleId)));
        Assert.Equal(1, await collection.CountDocumentsAsync(new BsonDocument("_id", liveId)));

        await collection.DeleteOneAsync(new BsonDocument("_id", liveId));
    }
}
