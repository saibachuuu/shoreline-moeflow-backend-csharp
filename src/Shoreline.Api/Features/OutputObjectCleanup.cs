using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

/// <summary>
/// Physical deletion and byte accounting for one batch of outputs. Kept
/// separate from ProjectContentCleanup because the output API must not edit
/// that shared lifecycle file, while preserving its exact key/accounting
/// contract.
/// </summary>
public static class OutputObjectCleanup
{
    /// <summary>
    /// Guard for the legacy default-only overloads. Callers that have a
    /// <see cref="ProjectStorageRouter"/> must use the pool-aware overload
    /// instead; this guard keeps an accidental default-bucket delete
    /// impossible for calls that forgot the router.
    /// </summary>
    public static void EnsureDefaultStorageSource(BsonDocument project)
    {
        var sourceId = project.GetValue("csid", BsonNull.Value);
        if (sourceId.IsBsonNull) return;
        if (sourceId.IsString && sourceId.AsString.Length == 0) return;
        throw new ApiError("OutputStorageUnavailable", 9003,
            "项目使用的存储来源暂不支持导出，请稍后重试", 503);
    }

    public static string? ObjectKey(ProjectCleanupOptions options, BsonDocument output)
    {
        var fileName = output.Text("file_name");
        return fileName.Length == 0
            ? null
            : options.OutputPrefix + output["_id"].AsObjectId + "/" + fileName;
    }

    public static Task DeleteObjectsStrictAsync(
        MongoStore store,
        ObjectStorage storage,
        ProjectCleanupOptions options,
        BsonDocument project,
        IReadOnlyList<BsonDocument> outputs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return DeleteObjectsStrictAsync(store, options, storage.DeleteAsync, project, outputs, ct);
    }

    /// <summary>
    /// Python Output.delete_real_files_strict: every object is removed before
    /// any row is deleted, and byte accounting is released only after its
    /// object delete succeeded. A failure propagates and leaves rows untouched.
    /// </summary>
    public static async Task DeleteObjectsStrictAsync(
        MongoStore store,
        ProjectCleanupOptions options,
        Func<string, CancellationToken, Task> deleteObject,
        BsonDocument project,
        IReadOnlyList<BsonDocument> outputs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deleteObject);

        foreach (var output in outputs)
        {
            var key = ObjectKey(options, output);
            if (key is not null) await deleteObject(key, ct);
        }
        foreach (var output in outputs)
        {
            var projectId = output.GetValue("p", project["_id"]).AsObjectId;
            await ReleaseOutputBytesAsync(store, projectId, output, ct);
        }
    }

    /// <summary>
    /// Python Output._release_project_bytes idempotency: claim <c>sb</c> with a
    /// CAS to 0 before deducting, and deduct KB with banker's rounding.
    /// </summary>
    public static async Task ReleaseOutputBytesAsync(
        MongoStore store,
        ObjectId projectId,
        BsonDocument output,
        CancellationToken ct)
    {
        var rawSize = output.GetValue("sb", BsonNull.Value);
        if (!rawSize.IsNumeric) return;
        var size = rawSize.ToInt64();
        if (size <= 0) return;

        var outputs = store.Collection("output");
        var claimed = await outputs.UpdateOneAsync(
            new BsonDocument { ["_id"] = output["_id"], ["sb"] = size },
            new BsonDocument("$set", new BsonDocument("sb", 0L)),
            cancellationToken: ct);
        if (claimed.ModifiedCount == 0) return;

        var kilobytes = (long)Math.Round(size / 1024.0, MidpointRounding.ToEven);
        await store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", projectId),
            new BsonDocument("$inc", new BsonDocument { ["ob"] = -size, ["fs"] = -kilobytes }),
            cancellationToken: ct);
    }
}
