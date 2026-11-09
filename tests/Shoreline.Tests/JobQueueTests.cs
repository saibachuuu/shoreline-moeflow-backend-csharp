using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Jobs;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class JobQueueTests(LocalServices services)
{
    [Fact]
    public async Task ConcurrentWorkersClaimOnceAndStaleOwnerCannotFinish()
    {
        var clock = new TestClock();
        var queue = new JobQueue(services.Store, clock);
        var kind = Guid.NewGuid().ToString("N");
        var id = await queue.EnqueueAsync(kind, new BsonDocument("test", true), default);
        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => queue.ClaimAsync("worker-" + i, [kind], default)));
        var winner = Assert.Single(claims, row => row is not null)!;
        var owner = winner["owner"].AsString;
        Assert.Equal(1, winner["attempts"].AsInt32);
        clock.Now = clock.Now.AddMinutes(3);
        var reclaimed = await queue.ClaimAsync("replacement", [kind], default);
        Assert.NotNull(reclaimed);
        Assert.Equal(2, reclaimed["attempts"].AsInt32);
        Assert.False(await queue.RenewAsync(id, owner, default));
        await queue.CompleteAsync(id, owner, default);
        Assert.Equal("running", (await Row(id))["state"].AsString);
        await queue.CompleteAsync(id, "replacement", default);
        Assert.Equal("done", (await Row(id))["state"].AsString);
    }

    [Fact]
    public async Task PendingCancellationPreventsClaimAndDoesNotCancelRunning()
    {
        var queue = new JobQueue(services.Store, new TestClock());
        var kind = Guid.NewGuid().ToString("N");
        var id = await queue.EnqueueAsync(kind, [], default);
        await queue.CancelPendingAsync(id, default);
        Assert.Null(await queue.ClaimAsync("worker", [kind], default));
        var running = await queue.EnqueueAsync(kind, [], default);
        await queue.ClaimAsync("worker", [kind], default);
        await queue.CancelPendingAsync(running, default);
        Assert.Equal("running", (await Row(running))["state"].AsString);
        await queue.CompleteAsync(running, "worker", default);
    }

    [Fact]
    public async Task FailureBackoffAndFinalInterruptedAttemptBecomeTerminal()
    {
        var clock = new TestClock();
        var queue = new JobQueue(services.Store, clock);
        var kind = Guid.NewGuid().ToString("N");
        var id = await queue.EnqueueAsync(kind, [], default);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.NotNull(await queue.ClaimAsync("worker", [kind], default));
            if (attempt == 5) break; // Simulate a crash on the final attempt.
            await queue.FailAsync(id, "worker", attempt, default);
            Assert.Null(await queue.ClaimAsync("worker", [kind], default));
            clock.Now = clock.Now.AddMinutes(6);
        }
        clock.Now = clock.Now.AddMinutes(3);
        Assert.Null(await queue.ClaimAsync("replacement", [kind], default));
        Assert.Equal("failed", (await Row(id))["state"].AsString);
    }

    private Task<BsonDocument> Row(ObjectId id) => services.Store.Collection("csharp_job")
        .Find(new BsonDocument("_id", id)).FirstAsync();
}
