using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

/// <summary>
/// Accounting phases whose write has been committed. The optional observer is a
/// test seam for the unknown-result window: it runs after the server acknowledged
/// the write but before the method returns, so a throwing observer models a lost
/// acknowledgement on an already-committed operation.
/// </summary>
public enum OutputAccountingWritePhase
{
    ArtifactRecorded,
    ProjectLedgerApplied,
    SuccessMarked
}

/// <summary>
/// Crash-consistent accounting for an output artifact.
///
/// The output row is the durable per-output record: <c>sb</c> and
/// <c>job_accounting</c> are written before the project counters move. The
/// project document is then updated with the total of all output sizes rather
/// than an incremental delta, and that single-document update both applies the
/// counters and acts as the idempotency check (the update is conditional on
/// the project's current <c>ob</c>/<c>fs</c> values). Only after that succeeds
/// is the output marked <c>s=4</c>. A crash at any point therefore leaves the
/// output in a retryable state and a replay converges on the same totals.
///
/// This uses the deterministic rebuild of the output portion of the project
/// ledger: <c>ob = sum(output.sb)</c> and <c>fs</c> is adjusted by the rounded
/// difference. There is no permanent operation ledger.
/// </summary>
public static class OutputAccounting
{
    public const int MaxProjectApplyAttempts = 8;

    /// <summary>
    /// Persist the uploaded artifact's byte count and durable pending marker.
    /// The output is deliberately not marked succeeded here.
    /// </summary>
    public static async Task<bool> RecordArtifactAsync(
        MongoStore store,
        ObjectId outputId,
        long bytes,
        string fileName,
        DateTime now,
        FilterDefinition<BsonDocument> guard,
        CancellationToken ct,
        Func<OutputAccountingWritePhase, Task>? afterWrite = null)
    {
        var update = new BsonDocument("$set", new BsonDocument
        {
            ["sb"] = bytes,
            ["file_name"] = fileName,
            ["job_accounting"] = new BsonDocument
            {
                ["bytes"] = bytes,
                ["recorded_at"] = now
            }
        });
        var result = await store.Collection("output").UpdateOneAsync(guard, update, cancellationToken: ct);
        // A retry can write exactly the same sb/file_name/marker (for example
        // when the clock is fixed), in which case MongoDB reports a match but
        // no modification. The guard matching is what proves ownership.
        if (result.MatchedCount == 1 && afterWrite is not null)
            await afterWrite(OutputAccountingWritePhase.ArtifactRecorded);
        return result.MatchedCount == 1;
    }

    /// <summary>
    /// Apply the project's output-byte ledger using the sum of every output's
    /// <c>sb</c>. The conditional update on <c>ob</c> and <c>fs</c> is the
    /// deduplication point: a replay sees the already-applied total and makes
    /// no further change. Returns false only if the project no longer exists.
    /// </summary>
    public static async Task<bool> ApplyProjectAccountingAsync(
        MongoStore store,
        ObjectId projectId,
        CancellationToken ct,
        Func<OutputAccountingWritePhase, Task>? afterWrite = null)
    {
        var outputs = store.Collection("output");
        var projects = store.Collection("project");

        for (var attempt = 0; attempt < MaxProjectApplyAttempts; attempt++)
        {
            var total = await OutputTotalAsync(outputs, projectId, ct);
            var project = await projects.Find(new BsonDocument("_id", projectId))
                .FirstOrDefaultAsync(ct);
            if (project is null) return false;

            var currentObValue = project.GetValue("ob", BsonNull.Value);
            var currentFsValue = project.GetValue("fs", BsonNull.Value);
            var currentOb = currentObValue.IsNumeric ? currentObValue.ToInt64() : 0L;
            var currentFs = currentFsValue.IsNumeric ? currentFsValue.ToInt64() : 0L;
            if (total == currentOb) return true;

            var delta = total - currentOb;
            var kilobytes = (long)Math.Round(delta / 1024.0, MidpointRounding.ToEven);
            var filter = new BsonDocument
            {
                ["_id"] = projectId,
                ["ob"] = project.GetValue("ob", BsonNull.Value),
                ["fs"] = project.GetValue("fs", BsonNull.Value)
            };
            var update = new BsonDocument("$set", new BsonDocument
            {
                ["ob"] = total,
                ["fs"] = currentFs + kilobytes
            });
            var result = await projects.UpdateOneAsync(filter, update, cancellationToken: ct);
            if (result.ModifiedCount == 1)
            {
                if (afterWrite is not null)
                    await afterWrite(OutputAccountingWritePhase.ProjectLedgerApplied);
                return true;
            }

            // A concurrent accounting/file-size update changed ob or fs.
            // Re-read and converge without ever incrementing twice.
        }

        throw new InvalidOperationException("Project output accounting did not converge.");
    }

    /// <summary>
    /// Mark the output succeeded only after the project ledger has been
    /// applied. The lease guard prevents a stale executor from claiming the
    /// success of a newer one.
    /// </summary>
    public static async Task<bool> MarkSucceededAsync(
        MongoStore store,
        ObjectId outputId,
        string fileName,
        DateTime now,
        FilterDefinition<BsonDocument> guard,
        CancellationToken ct,
        Func<OutputAccountingWritePhase, Task>? afterWrite = null)
    {
        var update = new BsonDocument
        {
            ["$set"] = new BsonDocument
            {
                ["s"] = OutputStatus.Succeeded,
                ["file_name"] = fileName,
                ["job_owner"] = BsonNull.Value,
                ["job_lease_until"] = now,
                ["job_completed_at"] = now,
                ["job_error"] = BsonNull.Value
            },
            ["$unset"] = new BsonDocument("job_accounting", "")
        };
        var result = await store.Collection("output").UpdateOneAsync(guard, update, cancellationToken: ct);
        if (result.ModifiedCount == 1 && afterWrite is not null)
            await afterWrite(OutputAccountingWritePhase.SuccessMarked);
        return result.ModifiedCount == 1;
    }

    private static async Task<long> OutputTotalAsync(
        IMongoCollection<BsonDocument> outputs,
        ObjectId projectId,
        CancellationToken ct)
    {
        var result = await outputs.Aggregate()
            .Match(new BsonDocument("p", projectId))
            .Group(new BsonDocument
            {
                ["_id"] = BsonNull.Value,
                ["total"] = new BsonDocument("$sum", "$sb")
            })
            .FirstOrDefaultAsync(ct);
        return result is null || !result.GetValue("total", BsonNull.Value).IsNumeric
            ? 0L
            : result["total"].ToInt64();
    }
}
