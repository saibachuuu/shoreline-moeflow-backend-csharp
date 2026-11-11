using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Jobs;

/// <summary>Point in the idempotent enqueue write where a test observer may inject a fault.</summary>
public enum JobQueueWritePhase
{
    BeforeWrite,
    AfterWrite
}

/// <summary>Test-only seam invoked around the single atomic upsert of <see cref="JobQueue.EnqueueOnceAsync"/>.</summary>
public delegate Task JobQueueWriteObserver(JobQueueWritePhase phase, ObjectId jobId, CancellationToken ct);

/// <summary>
/// The same job id already exists with a different kind or payload. The message never contains the payload.
/// </summary>
public sealed class JobEnqueueConflictException : InvalidOperationException
{
    public JobEnqueueConflictException(ObjectId jobId)
        : base($"Job queue entry '{jobId}' already exists with a different kind or payload.")
    {
        JobId = jobId;
    }

    public ObjectId JobId { get; }
}

public sealed class JobQueue(MongoStore store, TimeProvider clock, JobQueueWriteObserver? writeObserver = null, int maxAttempts = JobQueue.DefaultMaxAttempts)
{
    /// <summary>
    /// Default ceiling for delivery attempts of a single job (initial claim +
    /// retries). A job whose attempt counter reaches this value is moved to
    /// <c>failed</c> instead of being reclaimed. Overridable per instance via
    /// the <c>maxAttempts</c> constructor argument and process-wide via the
    /// <c>Jobs:MaxAttempts</c> configuration key (see Program.cs).
    /// Mirrors the Celery default-style bounded retry policy: exponential
    /// backoff (<see cref="RetryDelay"/>) with a hard attempt ceiling — never
    /// infinite retries.
    /// </summary>
    public const int DefaultMaxAttempts = 5;

    private readonly int maxAttempts = Math.Max(1, maxAttempts);

    /// <summary>
    /// Retry backoff for a failed attempt: <c>min(300s, 5 * 2^attempts)</c>,
    /// matching the Python Celery beat/worker retry posture (bounded
    /// exponential backoff, 5-minute cap).
    /// </summary>
    internal static TimeSpan RetryDelay(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(300, 5 * (1 << Math.Min(Math.Max(0, attempts), 20))));
    private IMongoCollection<BsonDocument> Jobs => store.Collection("csharp_job");
    public async Task<ObjectId> EnqueueAsync(string kind, BsonDocument payload, CancellationToken ct)
    {
        var id = ObjectId.GenerateNewId();
        await Jobs.InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["kind"] = kind,
            ["payload"] = payload,
            ["state"] = "pending",
            ["attempts"] = 0,
            ["available_at"] = clock.GetUtcNow().UtcDateTime,
            ["created_at"] = clock.GetUtcNow().UtcDateTime
        }, cancellationToken: ct);
        return id;
    }

    /// <summary>
    /// Enqueues a preallocated job id at most once. Repeated calls with the same id, kind and
    /// typed BSON payload return the id and leave the existing row untouched, including its state,
    /// attempts and timestamps. A different kind or payload throws
    /// <see cref="JobEnqueueConflictException"/> and never resets the row.
    /// </summary>
    /// <remarks>
    /// The insertion is a single atomic upsert filtered only by <c>_id</c> with <c>$setOnInsert</c>,
    /// so no extra unique index is required. Unknown write exceptions are propagated unchanged so the
    /// caller can retry the same id; a duplicate-key race is resolved by a bounded read-back.
    /// </remarks>
    public async Task<ObjectId> EnqueueOnceAsync(ObjectId jobId, string kind, BsonDocument payload, CancellationToken ct)
    {
        if (jobId == ObjectId.Empty)
            throw new ArgumentException("A non-empty job id is required.", nameof(jobId));
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("A non-blank job kind is required.", nameof(kind));
        ArgumentNullException.ThrowIfNull(payload);

        // Detach the persisted document from the caller's instance so later mutations cannot leak in.
        var snapshot = payload.DeepClone().AsBsonDocument;
        var now = clock.GetUtcNow().UtcDateTime;
        var insert = new BsonDocument
        {
            ["kind"] = kind,
            ["payload"] = snapshot,
            ["state"] = "pending",
            ["attempts"] = 0,
            ["available_at"] = now,
            ["created_at"] = now
        };

        await NotifyWriteObserverAsync(JobQueueWritePhase.BeforeWrite, jobId, ct);

        UpdateResult result;
        try
        {
            result = await Jobs.UpdateOneAsync(
                new BsonDocument("_id", jobId),
                new BsonDocument("$setOnInsert", insert),
                new UpdateOptions { IsUpsert = true },
                ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // A concurrent upsert inserted this _id between our filter evaluation and the insert.
            // That is a known race, so confirming the committed row is safe.
            return await VerifyExistingAsync(jobId, kind, snapshot, ct);
        }

        await NotifyWriteObserverAsync(JobQueueWritePhase.AfterWrite, jobId, ct);
        ct.ThrowIfCancellationRequested();

        if (result.UpsertedId != null)
            return jobId;
        if (result.MatchedCount == 0)
            throw new InvalidOperationException($"Job queue entry '{jobId}' could not be confirmed after enqueue.");
        return await VerifyExistingAsync(jobId, kind, snapshot, ct);
    }

    public async Task<BsonDocument?> ClaimAsync(string owner, string[] kinds, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var f = Builders<BsonDocument>.Filter;
        await Jobs.UpdateManyAsync(f.Eq("state", "running") & f.Lte("lease_until", now) & f.Gte("attempts", maxAttempts),
            Builders<BsonDocument>.Update.Set("state", "failed").Set("finished_at", now)
                .Unset("owner").Unset("lease_until"), cancellationToken: ct);
        var eligible = f.In("kind", kinds) & f.Lt("attempts", maxAttempts) &
            ((f.Eq("state", "pending") & f.Lte("available_at", now)) |
             (f.Eq("state", "running") & f.Lte("lease_until", now)));
        return await Jobs.FindOneAndUpdateAsync<BsonDocument>(eligible,
            Builders<BsonDocument>.Update.Set("state", "running").Set("owner", owner)
                .Set("lease_until", now.AddMinutes(2)).Inc("attempts", 1),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                ReturnDocument = ReturnDocument.After,
                Sort = Builders<BsonDocument>.Sort.Ascending("available_at").Ascending("_id")
            }, ct);
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
        Builders<BsonDocument>.Update.Set("state", attempts >= maxAttempts ? "failed" : "pending")
            .Set("available_at", clock.GetUtcNow().UtcDateTime.Add(RetryDelay(attempts)))
            .Unset("owner").Unset("lease_until"), cancellationToken: ct);

    public Task CancelPendingAsync(ObjectId id, CancellationToken ct) => Jobs.UpdateOneAsync(
        new BsonDocument { ["_id"] = id, ["state"] = "pending", ["cancellable"] = new BsonDocument("$ne", false) },
        Builders<BsonDocument>.Update.Set("state", "cancelled"), cancellationToken: ct);

    private Task NotifyWriteObserverAsync(JobQueueWritePhase phase, ObjectId jobId, CancellationToken ct) =>
        writeObserver is null ? Task.CompletedTask : writeObserver(phase, jobId, ct);

    private async Task<ObjectId> VerifyExistingAsync(ObjectId jobId, string kind, BsonDocument snapshot, CancellationToken ct)
    {
        var existing = await ReadExistingAsync(jobId, ct);
        if (!existing.TryGetValue("kind", out var storedKind) || !storedKind.IsString ||
            !string.Equals(storedKind.AsString, kind, StringComparison.Ordinal) ||
            !existing.TryGetValue("payload", out var storedPayload) || storedPayload.BsonType != BsonType.Document ||
            !StrictBsonEquals(storedPayload, snapshot))
        {
            throw new JobEnqueueConflictException(jobId);
        }

        ct.ThrowIfCancellationRequested();
        return jobId;
    }

    // A matched update proves the row is committed, but a duplicate-key race may be observed a moment
    // before the winning write is visible on this read. Bound the wait so uncertainty still throws.
    private async Task<BsonDocument> ReadExistingAsync(ObjectId jobId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var row = await Jobs.Find(new BsonDocument("_id", jobId)).FirstOrDefaultAsync(ct);
            if (row is not null)
                return row;
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException($"Job queue entry '{jobId}' could not be confirmed after enqueue.");
            await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
        }
    }

    // Strict typed BSON equality: BsonType must match at every node (Int32 != Int64 != Double),
    // document field order and array order are significant. Payloads are compared as BSON, never JSON.
    private static bool StrictBsonEquals(BsonValue left, BsonValue right)
    {
        if (left.BsonType != right.BsonType)
            return false;

        switch (left.BsonType)
        {
            case BsonType.Document:
            {
                var leftDocument = left.AsBsonDocument;
                var rightDocument = right.AsBsonDocument;
                if (leftDocument.ElementCount != rightDocument.ElementCount)
                    return false;
                for (var index = 0; index < leftDocument.ElementCount; index++)
                {
                    var leftElement = leftDocument.GetElement(index);
                    var rightElement = rightDocument.GetElement(index);
                    if (!string.Equals(leftElement.Name, rightElement.Name, StringComparison.Ordinal) ||
                        !StrictBsonEquals(leftElement.Value, rightElement.Value))
                    {
                        return false;
                    }
                }
                return true;
            }
            case BsonType.Array:
            {
                var leftArray = left.AsBsonArray;
                var rightArray = right.AsBsonArray;
                if (leftArray.Count != rightArray.Count)
                    return false;
                for (var index = 0; index < leftArray.Count; index++)
                {
                    if (!StrictBsonEquals(leftArray[index], rightArray[index]))
                        return false;
                }
                return true;
            }
            default:
                return left.Equals(right);
        }
    }

    private static BsonDocument Owned(ObjectId id, string owner) => new()
    {
        ["_id"] = id,
        ["owner"] = owner,
        ["state"] = "running"
    };
}
