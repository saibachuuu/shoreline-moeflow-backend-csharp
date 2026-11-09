using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Jobs;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class JobQueueIdempotencyTests(LocalServices services)
{
    private IMongoCollection<BsonDocument> Jobs => services.Store.Collection("csharp_job");

    [Fact]
    public async Task TwentyConcurrentEnqueuesOfTheSameIdCreateOnePendingRow()
    {
        var clock = new TestClock();
        var queue = new JobQueue(services.Store, clock);
        var id = ObjectId.GenerateNewId();
        var kind = Kind();
        var payload = Payload();

        var ids = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => queue.EnqueueOnceAsync(id, kind, payload, default)));

        Assert.All(ids, value => Assert.Equal(id, value));
        var row = Assert.Single(await Rows(id));
        Assert.Equal("pending", row["state"].AsString);
        Assert.Equal(0, row["attempts"].AsInt32);
        AssertBsonExact(payload, row["payload"]);
    }

    [Fact]
    public async Task SameIdWithDifferentKindConflictsAndKeepsTheFirstRow()
    {
        var queue = new JobQueue(services.Store, new TestClock());
        var id = ObjectId.GenerateNewId();
        var payload = Payload();
        await queue.EnqueueOnceAsync(id, Kind(), payload, default);
        var before = Assert.Single(await Rows(id));

        var conflict = await Assert.ThrowsAsync<JobEnqueueConflictException>(() =>
            queue.EnqueueOnceAsync(id, Kind(), payload, default));

        Assert.Equal(id, conflict.JobId);
        Assert.DoesNotContain("generation", conflict.Message);
        Assert.Equal(before.ToBson(), Assert.Single(await Rows(id)).ToBson());
    }

    [Fact]
    public async Task SameIdWithInt32ThenInt64PayloadConflictsOnExactBsonTypes()
    {
        var queue = new JobQueue(services.Store, new TestClock());
        var id = ObjectId.GenerateNewId();
        var kind = Kind();

        Assert.Equal(id, await queue.EnqueueOnceAsync(id, kind, new BsonDocument("generation", 7), default));
        Assert.Equal(id, await queue.EnqueueOnceAsync(id, kind, new BsonDocument("generation", 7), default));

        var conflict = await Assert.ThrowsAsync<JobEnqueueConflictException>(() =>
            queue.EnqueueOnceAsync(id, kind, new BsonDocument("generation", 7L), default));

        Assert.Equal(id, conflict.JobId);
        var row = Assert.Single(await Rows(id));
        Assert.Equal(BsonType.Int32, row["payload"].AsBsonDocument["generation"].BsonType);
    }

    [Fact]
    public async Task SameFieldsInDifferentDocumentOrderConflict()
    {
        var queue = new JobQueue(services.Store, new TestClock());
        var id = ObjectId.GenerateNewId();
        var kind = Kind();
        await queue.EnqueueOnceAsync(id, kind, new BsonDocument { ["first"] = 1, ["second"] = 2 }, default);

        await Assert.ThrowsAsync<JobEnqueueConflictException>(() =>
            queue.EnqueueOnceAsync(id, kind, new BsonDocument { ["second"] = 2, ["first"] = 1 }, default));

        Assert.Equal(0, (await Rows(id))[0]["attempts"].AsInt32);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("failed")]
    [InlineData("running")]
    [InlineData("cancelled")]
    public async Task ExistingRowInAnyStateIsReturnedWithoutResettingStateAttemptsOrTimestamps(string state)
    {
        var clock = new TestClock();
        var queue = new JobQueue(services.Store, clock);
        var id = ObjectId.GenerateNewId();
        var kind = Kind();
        var payload = Payload();
        await queue.EnqueueOnceAsync(id, kind, payload, default);

        var update = new BsonDocument { ["state"] = state, ["attempts"] = 3 };
        if (state == "running")
        {
            update["owner"] = "worker-a";
            update["lease_until"] = clock.GetUtcNow().AddMinutes(2).UtcDateTime;
        }
        else
        {
            update["finished_at"] = clock.GetUtcNow().AddMinutes(1).UtcDateTime;
        }
        await Jobs.UpdateOneAsync(new BsonDocument("_id", id), new BsonDocument("$set", update));
        var before = Assert.Single(await Rows(id));

        Assert.Equal(id, await queue.EnqueueOnceAsync(id, kind, payload, default));

        var after = Assert.Single(await Rows(id));
        Assert.Equal(state, after["state"].AsString);
        Assert.Equal(3, after["attempts"].AsInt32);
        // Byte-for-byte unchanged proves attempts, timestamps, owner and lease were not touched.
        Assert.Equal(before.ToBson(), after.ToBson());
    }

    [Fact]
    public async Task FailedRowWithDifferentPayloadConflictsAndStaysFailed()
    {
        var clock = new TestClock();
        var queue = new JobQueue(services.Store, clock);
        var id = ObjectId.GenerateNewId();
        var kind = Kind();
        await queue.EnqueueOnceAsync(id, kind, Payload(), default);
        await Jobs.UpdateOneAsync(new BsonDocument("_id", id), new BsonDocument("$set", new BsonDocument
        {
            ["state"] = "failed",
            ["attempts"] = 5,
            ["finished_at"] = clock.GetUtcNow().AddMinutes(1).UtcDateTime
        }));
        var before = Assert.Single(await Rows(id));

        await Assert.ThrowsAsync<JobEnqueueConflictException>(() =>
            queue.EnqueueOnceAsync(id, kind, new BsonDocument("generation", 8), default));

        var after = Assert.Single(await Rows(id));
        Assert.Equal("failed", after["state"].AsString);
        Assert.Equal(5, after["attempts"].AsInt32);
        Assert.Equal(before.ToBson(), after.ToBson());
    }

    [Fact]
    public async Task CommittedInsertFollowedByInjectedFaultIsSafeToRetry()
    {
        var clock = new TestClock();
        var faulting = new JobQueue(services.Store, clock, (phase, _, _) =>
            phase == JobQueueWritePhase.AfterWrite
                ? throw new InvalidOperationException("injected after write")
                : Task.CompletedTask);
        var queue = new JobQueue(services.Store, clock);
        var id = ObjectId.GenerateNewId();
        var kind = Kind();
        var payload = Payload();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            faulting.EnqueueOnceAsync(id, kind, payload, default));
        var committed = Assert.Single(await Rows(id));
        Assert.Equal("pending", committed["state"].AsString);
        Assert.Equal(0, committed["attempts"].AsInt32);

        Assert.Equal(id, await queue.EnqueueOnceAsync(id, kind, payload, default));
        Assert.Equal(id, await queue.EnqueueOnceAsync(id, kind, payload, default));

        var after = Assert.Single(await Rows(id));
        Assert.Equal(committed.ToBson(), after.ToBson());
    }

    [Fact]
    public async Task FaultBeforeWriteLeavesNoRowAndRetryInsertsOnce()
    {
        var clock = new TestClock();
        var faulting = new JobQueue(services.Store, clock, (phase, _, _) =>
            phase == JobQueueWritePhase.BeforeWrite
                ? throw new InvalidOperationException("injected before write")
                : Task.CompletedTask);
        var queue = new JobQueue(services.Store, clock);
        var id = ObjectId.GenerateNewId();
        var kind = Kind();
        var payload = Payload();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            faulting.EnqueueOnceAsync(id, kind, payload, default));
        Assert.Empty(await Rows(id));

        Assert.Equal(id, await queue.EnqueueOnceAsync(id, kind, payload, default));
        Assert.Single(await Rows(id));
    }

    [Fact]
    public async Task CancellationAfterCommitPropagatesAndRetryReturnsTheSameRow()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new TestClock();
        var queue = new JobQueue(services.Store, clock, (phase, _, _) =>
        {
            if (phase == JobQueueWritePhase.AfterWrite)
                cancellation.Cancel();
            return Task.CompletedTask;
        });
        var id = ObjectId.GenerateNewId();
        var kind = Kind();
        var payload = Payload();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queue.EnqueueOnceAsync(id, kind, payload, cancellation.Token));
        var committed = Assert.Single(await Rows(id));
        Assert.Equal("pending", committed["state"].AsString);
        Assert.Equal(0, committed["attempts"].AsInt32);

        var retry = new JobQueue(services.Store, clock);
        Assert.Equal(id, await retry.EnqueueOnceAsync(id, kind, payload, default));
        Assert.Equal(committed.ToBson(), Assert.Single(await Rows(id)).ToBson());
    }

    [Fact]
    public async Task MutatingCallerPayloadAfterEnqueueDoesNotChangeTheStoredPayload()
    {
        var queue = new JobQueue(services.Store, new TestClock());
        var id = ObjectId.GenerateNewId();
        var kind = Kind();
        var payload = Payload();
        var expected = payload.DeepClone().AsBsonDocument;

        await queue.EnqueueOnceAsync(id, kind, payload, default);

        payload["generation"] = 999;
        payload["nested"].AsBsonDocument["text"] = "mutated";
        payload["values"].AsBsonArray.Add("mutated");
        payload["added"] = true;

        var row = Assert.Single(await Rows(id));
        AssertBsonExact(expected, row["payload"]);
        Assert.False(row["payload"].AsBsonDocument.Contains("added"));
    }

    [Fact]
    public async Task InvalidIdAndBlankKindAreRejectedBeforeAnyWrite()
    {
        var observerCalls = 0;
        var queue = new JobQueue(services.Store, new TestClock(), (_, _, _) =>
        {
            observerCalls++;
            return Task.CompletedTask;
        });
        var id = ObjectId.GenerateNewId();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            queue.EnqueueOnceAsync(ObjectId.Empty, Kind(), Payload(), default));
        Assert.Empty(await Rows(ObjectId.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            queue.EnqueueOnceAsync(id, "", Payload(), default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            queue.EnqueueOnceAsync(id, "   ", Payload(), default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            queue.EnqueueOnceAsync(id, null!, Payload(), default));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            queue.EnqueueOnceAsync(id, Kind(), null!, default));

        Assert.Equal(0, observerCalls);
        Assert.Empty(await Rows(id));
    }

    private async Task<List<BsonDocument>> Rows(ObjectId id) =>
        await Jobs.Find(new BsonDocument("_id", id)).ToListAsync();

    private static string Kind() => Guid.NewGuid().ToString("N");

    private static BsonDocument Payload() => new()
    {
        ["generation"] = 7,
        ["nested"] = new BsonDocument { ["text"] = "shoreline", ["flag"] = true },
        ["values"] = new BsonArray { 1, 2L, "three" }
    };

    private static void AssertBsonExact(BsonValue expected, BsonValue actual) =>
        Assert.Equal(expected.ToBson(), actual.ToBson());
}
