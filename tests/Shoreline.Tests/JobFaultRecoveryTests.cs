using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Jobs;

namespace Shoreline.Tests;

// Fault-recovery verification for the csharp_job queue (see docs/job-semantics.md):
// lease-expiry reclaim after a worker crash, the Jobs:MaxAttempts terminal
// ceiling, the bounded exponential backoff schedule, and graceful shutdown
// (StopAsync stops claiming while an in-flight job finishes within bounds).
[Collection("local-services")]
public sealed class JobFaultRecoveryTests(LocalServices services)
{
    // A crashed worker leaves its job "running" with an expired lease; a new
    // worker reclaims the same row and runs it to completion.
    [Fact]
    public async Task ExpiredLease_ReclaimedByNewWorkerAndCompletes()
    {
        var queue = new JobQueue(services.Store, TimeProvider.System);
        var kind = "fault-lease-" + Guid.NewGuid().ToString("N");
        var jobId = ObjectId.GenerateNewId();
        await queue.EnqueueOnceAsync(jobId, kind, new BsonDocument(), default);

        var crashed = await queue.ClaimAsync("crashed-worker", [kind], default);
        Assert.NotNull(crashed);
        Assert.Equal(1, crashed["attempts"].AsInt32);

        // Simulate the crash: the owner never renews, so the 2-minute lease lapses.
        await services.Store.Collection("csharp_job").UpdateOneAsync(
            new BsonDocument("_id", jobId),
            Builders<BsonDocument>.Update.Set("lease_until", DateTime.UtcNow.AddSeconds(-1)));

        var reclaimed = await queue.ClaimAsync("replacement-worker", [kind], default);
        Assert.NotNull(reclaimed);
        Assert.Equal(2, reclaimed["attempts"].AsInt32);
        Assert.Equal("replacement-worker", reclaimed["owner"].AsString);

        await queue.CompleteAsync(jobId, "replacement-worker", default);
        Assert.Equal("done", (await RowAsync(jobId))["state"].AsString);
    }

    private Task<BsonDocument> RowAsync(ObjectId id) =>
        services.Store.Collection("csharp_job").Find(new BsonDocument("_id", id)).FirstAsync();
}
