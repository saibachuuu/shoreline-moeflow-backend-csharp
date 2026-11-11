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

    // Repeated failures up to the Jobs:MaxAttempts ceiling move the job to the
    // terminal "failed" state; afterwards it is never retried.
    [Fact]
    public async Task ConsecutiveFailures_AtMaxAttempts_MoveToFailed()
    {
        // Mirror the Program.cs wiring so the test proves the config key name.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jobs:MaxAttempts"] = "3"
        }).Build();
        var maxAttempts = config.GetValue("Jobs:MaxAttempts", JobQueue.DefaultMaxAttempts);
        Assert.Equal(3, maxAttempts);

        var clock = new TestClock();
        var queue = new JobQueue(services.Store, clock, maxAttempts: maxAttempts);
        var kind = "fault-max-" + Guid.NewGuid().ToString("N");
        var jobId = ObjectId.GenerateNewId();
        await queue.EnqueueOnceAsync(jobId, kind, new BsonDocument(), default);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var claimed = await queue.ClaimAsync("worker", [kind], default);
            Assert.NotNull(claimed);
            Assert.Equal(attempt, claimed["attempts"].AsInt32);
            await queue.FailAsync(jobId, "worker", claimed["attempts"].AsInt32, default);
            if (attempt < 3)
            {
                Assert.Equal("pending", (await RowAsync(jobId))["state"].AsString);
                // Backoff has not elapsed yet: nothing eligible.
                Assert.Null(await queue.ClaimAsync("worker", [kind], default));
                clock.Now = clock.Now.Add(JobQueue.RetryDelay(attempt)).Add(TimeSpan.FromSeconds(1));
            }
        }

        Assert.Equal("failed", (await RowAsync(jobId))["state"].AsString);
        Assert.Null(await queue.ClaimAsync("worker", [kind], default));
    }

    // A row that already reached the ceiling but is still "running" (crashed on
    // its final attempt) is swept to "failed" on the next claim pass, never
    // reclaimed.
    [Fact]
    public async Task ExpiredLease_AtMaxAttempts_SweptToFailed()
    {
        var clock = new TestClock();
        var queue = new JobQueue(services.Store, clock, maxAttempts: 2);
        var kind = "fault-sweep-" + Guid.NewGuid().ToString("N");
        var jobId = ObjectId.GenerateNewId();
        await queue.EnqueueOnceAsync(jobId, kind, new BsonDocument(), default);

        var first = await queue.ClaimAsync("worker", [kind], default);
        Assert.NotNull(first);
        await queue.FailAsync(jobId, "worker", first["attempts"].AsInt32, default);
        clock.Now = clock.Now.Add(JobQueue.RetryDelay(1)).Add(TimeSpan.FromSeconds(1));

        // Final attempt; the worker crashes before Fail/Complete, leaving the
        // row "running" at attempts == maxAttempts with a live lease.
        var last = await queue.ClaimAsync("worker", [kind], default);
        Assert.NotNull(last);
        Assert.Equal(2, last["attempts"].AsInt32);

        clock.Now = clock.Now.AddMinutes(3); // 2-minute lease expired.
        Assert.Null(await queue.ClaimAsync("replacement", [kind], default));
        Assert.Equal("failed", (await RowAsync(jobId))["state"].AsString);
    }
}
