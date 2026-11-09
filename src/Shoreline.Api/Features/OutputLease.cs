using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Api.Features;

/// <summary>
/// Per-output execution lease on top of the job queue lease. It prevents a
/// duplicate/retried job from running the same export twice and makes the
/// final publish a compare-and-set owned by the executor that did the work.
/// </summary>
public static class OutputLease
{
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(2);

    private static FilterDefinition<BsonDocument> Owned(ObjectId id, string owner, DateTime now) =>
        Builders<BsonDocument>.Filter.Eq("_id", id)
        & Builders<BsonDocument>.Filter.Eq("job_owner", owner)
        & Builders<BsonDocument>.Filter.Eq("job_run_token", owner)
        & Builders<BsonDocument>.Filter.Gt("job_lease_until", now)
        & Builders<BsonDocument>.Filter.Ne("s", OutputStatus.Succeeded);

    public static async Task<bool> TryClaimAsync(
        IMongoCollection<BsonDocument> outputs,
        ObjectId outputId,
        string owner,
        DateTime now,
        CancellationToken ct)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", outputId)
                     & Builders<BsonDocument>.Filter.Ne("s", OutputStatus.Succeeded)
                     & Builders<BsonDocument>.Filter.Or(
                         Builders<BsonDocument>.Filter.Eq("job_lease_until", BsonNull.Value),
                         Builders<BsonDocument>.Filter.Lt("job_lease_until", now));
        var update = new BsonDocument("$set", new BsonDocument
        {
            ["s"] = OutputStatus.Queuing,
            ["job_owner"] = owner,
            ["job_run_token"] = owner,
            ["job_started_at"] = now,
            ["job_lease_until"] = now + Duration
        });
        var result = await outputs.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public static async Task<bool> RenewAsync(
        IMongoCollection<BsonDocument> outputs,
        ObjectId outputId,
        string owner,
        DateTime now,
        CancellationToken ct)
    {
        var result = await outputs.UpdateOneAsync(
            Owned(outputId, owner, now),
            new BsonDocument("$set", new BsonDocument("job_lease_until", now + Duration)),
            cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public static async Task<bool> SetStatusAsync(
        IMongoCollection<BsonDocument> outputs,
        ObjectId outputId,
        string owner,
        int status,
        DateTime now,
        CancellationToken ct)
    {
        var result = await outputs.UpdateOneAsync(
            Owned(outputId, owner, now),
            new BsonDocument("$set", new BsonDocument
            {
                ["s"] = status,
                ["job_lease_until"] = now + Duration
            }),
            cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    /// <summary>Only the still-current owner may mark the export as failed.</summary>
    public static async Task<bool> MarkErrorAsync(
        IMongoCollection<BsonDocument> outputs,
        ObjectId outputId,
        string owner,
        DateTime now,
        CancellationToken ct)
    {
        var result = await outputs.UpdateOneAsync(
            Owned(outputId, owner, now),
            new BsonDocument("$set", new BsonDocument
            {
                ["s"] = OutputStatus.Error,
                ["job_lease_until"] = now
            }),
            cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    /// <summary>Release a cancelled run without claiming a durable error.</summary>
    public static async Task ReleaseAsync(
        IMongoCollection<BsonDocument> outputs,
        ObjectId outputId,
        string owner,
        DateTime now,
        CancellationToken ct)
    {
        await outputs.UpdateOneAsync(
            Owned(outputId, owner, now),
            new BsonDocument("$set", new BsonDocument("job_lease_until", now)),
            cancellationToken: ct);
    }

    /// <summary>Filter for the final publish compare-and-set.</summary>
    public static FilterDefinition<BsonDocument> PublishFilter(ObjectId outputId, string owner, DateTime now) =>
        Owned(outputId, owner, now);
}
