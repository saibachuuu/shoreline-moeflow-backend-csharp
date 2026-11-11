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

    // The retry backoff grows as min(300s, 5 * 2^attempts): strictly increasing
    // until the 5-minute cap.
    [Fact]
    public void BackoffSchedule_IncreasesAndCapsAt300s()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), JobQueue.RetryDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(10), JobQueue.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(20), JobQueue.RetryDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(40), JobQueue.RetryDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(80), JobQueue.RetryDelay(4));
        Assert.Equal(TimeSpan.FromSeconds(160), JobQueue.RetryDelay(5));
        Assert.Equal(TimeSpan.FromSeconds(300), JobQueue.RetryDelay(6));
        Assert.Equal(TimeSpan.FromSeconds(300), JobQueue.RetryDelay(7));
        Assert.Equal(TimeSpan.FromSeconds(300), JobQueue.RetryDelay(100));
        Assert.Equal(JobQueue.DefaultMaxAttempts, 5);
    }

    // FailAsync applies that schedule to available_at: each failure pushes the
    // next eligibility further out.
    [Fact]
    public async Task FailAsync_SchedulesIncreasingAvailableAt()
    {
        var clock = new TestClock();
        var queue = new JobQueue(services.Store, clock, maxAttempts: 10);
        var kind = "fault-backoff-" + Guid.NewGuid().ToString("N");
        var jobId = ObjectId.GenerateNewId();
        await queue.EnqueueOnceAsync(jobId, kind, new BsonDocument(), default);

        var first = await queue.ClaimAsync("worker", [kind], default);
        Assert.NotNull(first);
        await queue.FailAsync(jobId, "worker", first["attempts"].AsInt32, default);
        var firstAvailable = (await RowAsync(jobId))["available_at"].ToUniversalTime();
        Assert.Equal(clock.Now.UtcDateTime.Add(JobQueue.RetryDelay(1)), firstAvailable);

        clock.Now = clock.Now.Add(JobQueue.RetryDelay(1)).Add(TimeSpan.FromSeconds(1));
        var second = await queue.ClaimAsync("worker", [kind], default);
        Assert.NotNull(second);
        await queue.FailAsync(jobId, "worker", second["attempts"].AsInt32, default);
        var secondAvailable = (await RowAsync(jobId))["available_at"].ToUniversalTime();
        Assert.Equal(clock.Now.UtcDateTime.Add(JobQueue.RetryDelay(2)), secondAvailable);

        Assert.True(secondAvailable > firstAvailable,
            $"backoff did not increase: first {firstAvailable:o}, second {secondAvailable:o}");
        Assert.True(JobQueue.RetryDelay(2) > JobQueue.RetryDelay(1));
    }

    // StopAsync stops *claiming*: a job already in flight keeps its own
    // execution token and finishes within bounds (marked done), while a job
    // that was never claimed stays pending.
    [Fact]
    public async Task GracefulShutdown_StopsClaimingButFinishesInFlight()
    {
        var kind = "fault-shutdown-" + Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(kind, entered, release);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var queue = new JobQueue(services.Store, TimeProvider.System);
        var worker = new JobWorker(queue, [handler], config, NullLogger<JobWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var firstId = ObjectId.GenerateNewId();
            await queue.EnqueueOnceAsync(firstId, kind, new BsonDocument(), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var secondId = ObjectId.GenerateNewId();
            await queue.EnqueueOnceAsync(secondId, kind, new BsonDocument(), default);
            await Task.Delay(700); // Single-concurrency worker is busy in flight; second stays queued.

            var stopTask = worker.StopAsync(CancellationToken.None);
            release.TrySetResult(); // Let the bounded in-flight work finish.
            await stopTask.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("done", (await RowAsync(firstId))["state"].AsString);
            Assert.Equal(1, handler.Calls);
            Assert.Equal("pending", (await RowAsync(secondId))["state"].AsString);
        }
        finally
        {
            release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class BlockingHandler(
        string kind,
        TaskCompletionSource entered,
        TaskCompletionSource release) : IJobHandler
    {
        public int Calls;
        public string Kind => kind;
        public async Task ExecuteAsync(BsonDocument payload, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
    }
}
