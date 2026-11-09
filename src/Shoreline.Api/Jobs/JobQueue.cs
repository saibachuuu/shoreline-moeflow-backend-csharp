using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Jobs;

public sealed class JobQueue(MongoStore store, TimeProvider clock)
{
    private IMongoCollection<BsonDocument> Jobs => store.Collection("csharp_job");
    public async Task<ObjectId> EnqueueAsync(string kind, BsonDocument payload, CancellationToken ct)
    {
        var id = ObjectId.GenerateNewId();
        await Jobs.InsertOneAsync(new BsonDocument
        {
            ["_id"] = id, ["kind"] = kind, ["payload"] = payload, ["state"] = "pending",
            ["attempts"] = 0, ["available_at"] = clock.GetUtcNow().UtcDateTime,
            ["created_at"] = clock.GetUtcNow().UtcDateTime
        }, cancellationToken: ct);
        return id;
    }

    public async Task<BsonDocument?> ClaimAsync(string owner, string[] kinds, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var f = Builders<BsonDocument>.Filter;
        await Jobs.UpdateManyAsync(f.Eq("state", "running") & f.Lte("lease_until", now) & f.Gte("attempts", 5),
            Builders<BsonDocument>.Update.Set("state", "failed").Set("finished_at", now)
                .Unset("owner").Unset("lease_until"), cancellationToken: ct);
        var eligible = f.In("kind", kinds) & f.Lt("attempts", 5) &
            ((f.Eq("state", "pending") & f.Lte("available_at", now)) |
             (f.Eq("state", "running") & f.Lte("lease_until", now)));
        return await Jobs.FindOneAndUpdateAsync<BsonDocument>(eligible,
            Builders<BsonDocument>.Update.Set("state", "running").Set("owner", owner)
                .Set("lease_until", now.AddMinutes(2)).Inc("attempts", 1),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After,
                Sort = Builders<BsonDocument>.Sort.Ascending("available_at").Ascending("_id") }, ct);
    }

    public async Task<bool> RenewAsync(ObjectId id, string owner, CancellationToken ct)
    {
        var result = await Jobs.UpdateOneAsync(Owned(id, owner), Builders<BsonDocument>.Update
            .Set("lease_until", clock.GetUtcNow().UtcDateTime.AddMinutes(2)), cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public Task CompleteAsync(ObjectId id, string owner, CancellationToken ct) => Jobs.UpdateOneAsync(Owned(id, owner),
        Builders<BsonDocument>.Update.Set("state", "done").Set("finished_at", clock.GetUtcNow().UtcDateTime)
            .Unset("owner").Unset("lease_until"), cancellationToken: ct);

    public Task FailAsync(ObjectId id, string owner, int attempts, CancellationToken ct) => Jobs.UpdateOneAsync(Owned(id, owner),
        Builders<BsonDocument>.Update.Set("state", attempts >= 5 ? "failed" : "pending")
            .Set("available_at", clock.GetUtcNow().UtcDateTime.AddSeconds(Math.Min(300, 5 * (1 << attempts))))
            .Unset("owner").Unset("lease_until"), cancellationToken: ct);

    public Task CancelPendingAsync(ObjectId id, CancellationToken ct) => Jobs.UpdateOneAsync(
        new BsonDocument { ["_id"] = id, ["state"] = "pending" },
        Builders<BsonDocument>.Update.Set("state", "cancelled"), cancellationToken: ct);

    private static BsonDocument Owned(ObjectId id, string owner) => new()
    {
        ["_id"] = id, ["owner"] = owner, ["state"] = "running"
    };
}
