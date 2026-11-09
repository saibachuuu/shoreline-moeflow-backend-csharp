using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

/// <summary>
/// Object names and relative key prefixes used when a project's files/outputs
/// are physically removed. The defaults mirror Python app/config.py and
/// app/models/file.py / output.py; the global <c>Storage:KeyPrefix</c> is added
/// by <see cref="ObjectStorage"/> itself, never here.
/// </summary>
public sealed record ProjectCleanupOptions(
    string FilePrefix,
    string CoverPrefix,
    string ResamplePrefix,
    string OutputPrefix)
{
    public static ProjectCleanupOptions FromConfiguration(IConfiguration? configuration) => new(
        Prefix(configuration, "Storage:FilePrefix", "files/"),
        Prefix(configuration, "Storage:CoverPrefix", "cover"),
        Prefix(configuration, "Storage:ResamplePrefix", "resample"),
        Prefix(configuration, "Storage:OutputPrefix", "outputs/"));

    private static string Prefix(IConfiguration? configuration, string key, string fallback)
    {
        var value = configuration?[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}

/// <summary>
/// Physical cleanup shared by project whole-content clear and target deletion.
///
/// Legacy Python BSON field names are authoritative here:
///   file.project  = "p", file.type = "t" (FOLDER = 1), file.save_name = "sa"
///   output.project = "p", output.target = "t", output.storage_bytes = "sb"
///   file_target_cache.file = "f", file_target_cache.target = "t"
///   source.file = "f", translation.source/tip.source = "o",
///   translation.target/tip.target = "t"
///
/// Kept as a static helper (and constructed with the already-registered
/// ObjectStorage/config) so ProjectLifecycleService and ProjectService do not
/// require a new DI registration.
/// </summary>
public static class ProjectContentCleanup
{
    private const int BatchSize = 100;
    private const int FileTypeFolder = 1;

    public static Task ClearProjectContentsAsync(
        MongoStore store,
        ObjectStorage storage,
        IConfiguration? configuration,
        BsonDocument project,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return ClearProjectContentsAsync(
            store,
            ProjectCleanupOptions.FromConfiguration(configuration),
            storage.DeleteAsync,
            project,
            ct);
    }

    /// <summary>
    /// Python ProjectLifecycleService.clear_contents: delete originals and
    /// derived images for every file revision (folders have no object), cascade
    /// source/translation/tip/file_target_cache rows, delete output objects
    /// strictly, then delete output rows, then zero the nine content counters.
    /// Any storage failure propagates and leaves the unprocessed rows for retry.
    /// </summary>
    public static async Task ClearProjectContentsAsync(
        MongoStore store,
        ProjectCleanupOptions options,
        Func<string, CancellationToken, Task> deleteObject,
        BsonDocument project,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deleteObject);
        EnsureDefaultStorageSource(project);

        var projectId = project["_id"].AsObjectId;
        await DeleteAllFileRowsAsync(store, options, deleteObject, projectId, ct);
        await DeleteAllOutputRowsAsync(store, options, deleteObject, projectId, ct);
        await ZeroContentCountersAsync(store, projectId, ct);
    }

    /// <summary>
    /// Python FileTargetCache creation: one cache row for every file of the
    /// project, including folders and inactive/deactivated revisions.
    /// </summary>
    public static async Task CreateTargetCachesAsync(
        MongoStore store,
        BsonDocument project,
        BsonDocument target,
        DateTime editTime,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var projectId = project["_id"].AsObjectId;
        var targetId = target["_id"].AsObjectId;
        var files = store.Collection("file");
        var caches = store.Collection("file_target_cache");
        BsonValue lastId = BsonNull.Value;
        while (true)
        {
            var filter = new BsonDocument("p", projectId);
            if (!lastId.IsBsonNull) filter["_id"] = new BsonDocument("$gt", lastId);
            var batch = await files.Find(filter)
                .Sort(new BsonDocument("_id", 1))
                .Limit(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            var documents = batch.Select(file => new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["f"] = file["_id"],
                ["t"] = targetId,
                ["ts"] = 0,
                ["cs"] = 0,
                ["e"] = editTime
            }).ToList();
            await caches.InsertManyAsync(documents, cancellationToken: ct);
            lastId = batch[^1]["_id"];
        }
    }

    public static Task DeleteTargetCascadesAsync(
        MongoStore store,
        ObjectStorage storage,
        IConfiguration? configuration,
        BsonDocument project,
        BsonDocument target,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return DeleteTargetCascadesAsync(
            store,
            ProjectCleanupOptions.FromConfiguration(configuration),
            storage.DeleteAsync,
            project,
            target,
            ct);
    }

    /// <summary>
    /// Python Target.clear: output objects are deleted best-effort (as
    /// Output.delete_real_files, unlike the strict project clear), released
    /// bytes are only deducted after a successful delete, then ORM CASCADE rows
    /// (output/translation/tip/file_target_cache) are removed, the target row is
    /// deleted, and target_count is decremented only when that delete matched.
    /// </summary>
    public static async Task DeleteTargetCascadesAsync(
        MongoStore store,
        ProjectCleanupOptions options,
        Func<string, CancellationToken, Task> deleteObject,
        BsonDocument project,
        BsonDocument target,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deleteObject);
        EnsureDefaultStorageSource(project);

        var projectId = project["_id"].AsObjectId;
        var targetId = target["_id"].AsObjectId;

        await DeleteTargetOutputsBestEffortAsync(store, options, deleteObject, projectId, targetId, ct);

        await store.Collection("output").DeleteManyAsync(
            new BsonDocument { ["p"] = projectId, ["t"] = targetId }, ct);
        await store.Collection("translation").DeleteManyAsync(
            new BsonDocument("t", targetId), ct);
        await store.Collection("tip").DeleteManyAsync(
            new BsonDocument("t", targetId), ct);
        await store.Collection("file_target_cache").DeleteManyAsync(
            new BsonDocument("t", targetId), ct);

        var deleted = await store.Collection("target").DeleteOneAsync(
            new BsonDocument { ["_id"] = targetId, ["t"] = projectId }, ct);
        if (deleted.DeletedCount == 1)
        {
            await store.Collection("project").UpdateOneAsync(
                new BsonDocument("_id", projectId),
                new BsonDocument("$inc", new BsonDocument("tc", -1)),
                cancellationToken: ct);
        }
    }

    // ---- file cleanup ----

    private static async Task DeleteAllFileRowsAsync(
        MongoStore store,
        ProjectCleanupOptions options,
        Func<string, CancellationToken, Task> deleteObject,
        ObjectId projectId,
        CancellationToken ct)
    {
        var files = store.Collection("file");
        BsonValue lastId = BsonNull.Value;
        while (true)
        {
            var filter = new BsonDocument("p", projectId);
            if (!lastId.IsBsonNull) filter["_id"] = new BsonDocument("$gt", lastId);
            var batch = await files.Find(filter)
                .Sort(new BsonDocument("_id", 1))
                .Limit(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            foreach (var file in batch)
            {
                await DeleteFileObjectsAsync(options, deleteObject, file, ct);
                await DeleteFileRowsAsync(store, projectId, file["_id"].AsObjectId, ct);
            }

            lastId = batch[^1]["_id"];
        }
    }

    private static async Task DeleteFileObjectsAsync(
        ProjectCleanupOptions options,
        Func<string, CancellationToken, Task> deleteObject,
        BsonDocument file,
        CancellationToken ct)
    {
        // Python FileType.FOLDER == 1; folders never own a stored object.
        if (file.Integer("t") == FileTypeFolder) return;
        var saveName = file.Text("sa");
        if (saveName.Length == 0) return;

        // Original object.
        await deleteObject(options.FilePrefix + saveName, ct);

        // Python _delete_stored_objects always removes the local/derived cover
        // and resample keys. proofread-*.webp is intentionally NOT touched: those
        // links are long-lived in notification e-mails.
        var stem = Stem(saveName);
        await deleteObject(options.FilePrefix + options.CoverPrefix + "-" + stem + ".webp", ct);
        await deleteObject(options.FilePrefix + options.ResamplePrefix + "-" + stem + ".webp", ct);
    }

    private static string Stem(string saveName)
    {
        var dot = saveName.LastIndexOf('.');
        return dot < 0 ? saveName : saveName[..dot];
    }

    private static async Task DeleteFileRowsAsync(
        MongoStore store,
        ObjectId projectId,
        ObjectId fileId,
        CancellationToken ct)
    {
        var sources = store.Collection("source");
        var translations = store.Collection("translation");
        var tips = store.Collection("tip");

        // Bounded cascade by source ids: translation/tip use source db_field "o";
        // neither row belongs to another project's file.
        BsonValue lastSourceId = BsonNull.Value;
        while (true)
        {
            var filter = new BsonDocument("f", fileId);
            if (!lastSourceId.IsBsonNull) filter["_id"] = new BsonDocument("$gt", lastSourceId);
            var batch = await sources.Find(filter)
                .Sort(new BsonDocument("_id", 1))
                .Limit(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            var ids = new BsonArray(batch.Select(source => source["_id"]));
            var inIds = new BsonDocument("$in", ids);
            await translations.DeleteManyAsync(new BsonDocument("o", inIds), ct);
            await tips.DeleteManyAsync(new BsonDocument("o", inIds), ct);
            await sources.DeleteManyAsync(new BsonDocument("_id", inIds), ct);
            lastSourceId = batch[^1]["_id"];
        }

        await store.Collection("file_target_cache").DeleteManyAsync(
            new BsonDocument("f", fileId), ct);
        await store.Collection("file").DeleteOneAsync(
            new BsonDocument { ["_id"] = fileId, ["p"] = projectId }, ct);
    }

    // ---- output cleanup ----

    private static async Task DeleteAllOutputRowsAsync(
        MongoStore store,
        ProjectCleanupOptions options,
        Func<string, CancellationToken, Task> deleteObject,
        ObjectId projectId,
        CancellationToken ct)
    {
        // Strict first pass: objects only. If anything fails, no output rows are
        // deleted yet, so the whole clear can be retried from the object stage.
        var outputs = store.Collection("output");
        BsonValue lastId = BsonNull.Value;
        while (true)
        {
            var filter = new BsonDocument("p", projectId);
            if (!lastId.IsBsonNull) filter["_id"] = new BsonDocument("$gt", lastId);
            var batch = await outputs.Find(filter)
                .Sort(new BsonDocument("_id", 1))
                .Limit(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            foreach (var output in batch)
                await DeleteOutputObjectAsync(options, deleteObject, output, ct);

            lastId = batch[^1]["_id"];
        }

        // Second pass: rows, in small scoped batches.
        lastId = BsonNull.Value;
        while (true)
        {
            var filter = new BsonDocument("p", projectId);
            if (!lastId.IsBsonNull) filter["_id"] = new BsonDocument("$gt", lastId);
            var batch = await outputs.Find(filter)
                .Sort(new BsonDocument("_id", 1))
                .Limit(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            var ids = new BsonArray(batch.Select(output => output["_id"]));
            await outputs.DeleteManyAsync(new BsonDocument
            {
                ["p"] = projectId,
                ["_id"] = new BsonDocument("$in", ids)
            }, ct);
            lastId = batch[^1]["_id"];
        }
    }

    private static Task DeleteOutputObjectAsync(
        ProjectCleanupOptions options,
        Func<string, CancellationToken, Task> deleteObject,
        BsonDocument output,
        CancellationToken ct)
    {
        var fileName = output.Text("file_name");
        if (fileName.Length == 0) return Task.CompletedTask;
        return deleteObject(options.OutputPrefix + output["_id"].AsObjectId + "/" + fileName, ct);
    }

    private static async Task DeleteTargetOutputsBestEffortAsync(
        MongoStore store,
        ProjectCleanupOptions options,
        Func<string, CancellationToken, Task> deleteObject,
        ObjectId projectId,
        ObjectId targetId,
        CancellationToken ct)
    {
        var outputs = store.Collection("output");
        var projects = store.Collection("project");
        BsonValue lastId = BsonNull.Value;
        while (true)
        {
            var filter = new BsonDocument { ["p"] = projectId, ["t"] = targetId };
            if (!lastId.IsBsonNull) filter["_id"] = new BsonDocument("$gt", lastId);
            var batch = await outputs.Find(filter)
                .Sort(new BsonDocument("_id", 1))
                .Limit(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            foreach (var output in batch)
            {
                try
                {
                    await DeleteOutputObjectAsync(options, deleteObject, output, ct);
                    await ReleaseOutputBytesAsync(projects, outputs, projectId, output, ct);
                }
                catch (Exception)
                {
                    // Python Target.clear uses Output.delete_real_files, which is
                    // best-effort. Keep any bytes not proven deleted; the target
                    // CASCADE below still removes the row.
                }
            }

            lastId = batch[^1]["_id"];
        }
    }

    private static async Task ReleaseOutputBytesAsync(
        IMongoCollection<BsonDocument> projects,
        IMongoCollection<BsonDocument> outputs,
        ObjectId projectId,
        BsonDocument output,
        CancellationToken ct)
    {
        var rawSize = output.GetValue("sb", BsonNull.Value);
        if (!rawSize.IsNumeric) return;
        var size = rawSize.ToInt64();
        if (size <= 0) return;

        // Claim the accounting first, then deduct, so a crash cannot deduct the
        // same output twice on retry. A failed project update leaves it
        // over-counted (safe) rather than falsely released.
        var claimed = await outputs.UpdateOneAsync(
            new BsonDocument { ["_id"] = output["_id"], ["sb"] = size },
            new BsonDocument("$set", new BsonDocument("sb", 0L)),
            cancellationToken: ct);
        if (claimed.ModifiedCount == 0) return;

        var kilobytes = (long)Math.Round(size / 1024.0);
        await projects.UpdateOneAsync(
            new BsonDocument("_id", projectId),
            new BsonDocument("$inc", new BsonDocument { ["ob"] = -size, ["fs"] = -kilobytes }),
            cancellationToken: ct);
    }

    // ---- shared guards ----

    private static void EnsureDefaultStorageSource(BsonDocument project)
    {
        var sourceId = project.GetValue("csid", BsonNull.Value);
        if (sourceId.IsBsonNull) return;
        if (sourceId.IsString && sourceId.AsString.Length == 0) return;

        // Storage-pool routing is not implemented yet. Deleting in the default
        // bucket would be wrong when the project lives on another source; fail
        // closed and leave every DB row/object untouched for a retry after the
        // router lands. The lifecycle layer converts this into a retryable 503.
        throw new InvalidOperationException(
            "Project storage_source_id is set, but the storage pool router is not implemented; refusing to delete objects from the default bucket.");
    }

    private static Task ZeroContentCountersAsync(MongoStore store, ObjectId projectId, CancellationToken ct) =>
        store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", projectId),
            new BsonDocument("$set", new BsonDocument
            {
                ["fs"] = 0L,
                ["cover_bytes"] = 0L,
                ["resample_bytes"] = 0L,
                ["ob"] = 0L,
                ["fo"] = 0,
                ["fc"] = 0,
                ["sc"] = 0,
                ["tsc"] = 0,
                ["csc"] = 0
            }),
            cancellationToken: ct);
}
