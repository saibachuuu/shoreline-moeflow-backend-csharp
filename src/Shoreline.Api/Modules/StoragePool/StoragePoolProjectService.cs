using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Modules.StoragePool;

public sealed class StoragePoolProjectService
{
    private readonly MongoStore store;
    private readonly StoragePoolSourceService sources;
    private readonly StoragePoolTransferEngine engine;
    private readonly StoragePoolTransferService transfers;

    public StoragePoolProjectService(
        MongoStore store,
        StoragePoolSourceService sources,
        StoragePoolTransferEngine engine,
        StoragePoolTransferService transfers)
    {
        this.store = store;
        this.sources = sources;
        this.engine = engine;
        this.transfers = transfers;
    }

    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private IMongoCollection<BsonDocument> Sources => store.Collection(StoragePoolConstants.SourceCollection);
    private IMongoCollection<BsonDocument> TransferRows => store.Collection(StoragePoolConstants.TransferCollection);

    public async Task<BsonDocument> MigrateAsync(string projectId, JsonElement data, CancellationToken ct)
    {
        var project = await ProjectAsync(projectId, ct).ConfigureAwait(false);
        RequireUnlocked(project);
        if (StoragePoolBson.ObjectId(project, "csid") is not null)
            throw new ApiError("StoragePoolNotAllowed", 5, "项目已位于存储来源中", 409);
        var sourceId = RequiredObjectId(data, "source_id");
        await RequireEnabledSourceAsync(sourceId, ct).ConfigureAwait(false);
        await EnsureNoConflictingTransferAsync(project["_id"].AsObjectId, sourceId,
            StoragePoolConstants.DirectionIn, null, ct).ConfigureAwait(false);
        var transfer = await engine.PrepareAsync(project["_id"].AsObjectId, sourceId,
            StoragePoolConstants.DirectionIn, null, ct).ConfigureAwait(false);
        MaybeDispatch(transfer);
        return StoragePoolTransferView.ToApi(transfer, project, await SourceAsync(sourceId, ct).ConfigureAwait(false), null);
    }

    public async Task<BsonDocument> RestoreAsync(string projectId, JsonElement data, CancellationToken ct)
    {
        _ = data;
        var project = await ProjectAsync(projectId, ct).ConfigureAwait(false);
        RequireUnlocked(project);
        var sourceId = StoragePoolBson.ObjectId(project, "csid")
            ?? throw new ApiError("StoragePoolNotAllowed", 5, "项目不在存储来源中", 409);
        await RequireEnabledSourceAsync(sourceId, ct).ConfigureAwait(false);
        await EnsureNoConflictingTransferAsync(project["_id"].AsObjectId, sourceId,
            StoragePoolConstants.DirectionOut, null, ct).ConfigureAwait(false);
        var transfer = await engine.PrepareAsync(project["_id"].AsObjectId, sourceId,
            StoragePoolConstants.DirectionOut, null, ct).ConfigureAwait(false);
        MaybeDispatch(transfer);
        return StoragePoolTransferView.ToApi(transfer, project, await SourceAsync(sourceId, ct).ConfigureAwait(false), null);
    }

    public async Task<BsonDocument> MoveAsync(string projectId, JsonElement data, CancellationToken ct)
    {
        var project = await ProjectAsync(projectId, ct).ConfigureAwait(false);
        RequireUnlocked(project);
        var current = StoragePoolBson.ObjectId(project, "csid");
        var targetText = StoragePoolJson.Has(data, "target_source_id")
            ? StoragePoolJson.OptionalString(data, "target_source_id")?.Trim() ?? ""
            : "";
        if (targetText.Length == 0 || string.Equals(targetText, "default", StringComparison.OrdinalIgnoreCase))
        {
            if (current is null) throw new ApiError("StoragePoolNotAllowed", 5, "项目已位于默认存储", 409);
            await RequireEnabledSourceAsync(current.Value, ct).ConfigureAwait(false);
            await EnsureNoConflictingTransferAsync(project["_id"].AsObjectId, current.Value,
                StoragePoolConstants.DirectionOut, null, ct).ConfigureAwait(false);
            var outbound = await engine.PrepareAsync(project["_id"].AsObjectId, current.Value,
                StoragePoolConstants.DirectionOut, null, ct).ConfigureAwait(false);
            MaybeDispatch(outbound);
            return StoragePoolTransferView.ToApi(outbound, project,
                await SourceAsync(current.Value, ct).ConfigureAwait(false), null);
        }
        if (!ObjectId.TryParse(targetText, out var targetId))
            throw ApiError.Field("target_source_id", "目标存储来源不存在");
        if (current == targetId)
            throw new ApiError("StoragePoolNotAllowed", 5, "目标存储来源与当前位置相同", 409);
        var target = await SourceAsync(targetId, ct).ConfigureAwait(false);
        if (!StoragePoolBson.Bool(target, "e", true))
            throw new ApiError("StoragePoolNotAllowed", 5, "目标存储来源已停用", 409);
        if (current is not null) await RequireEnabledSourceAsync(current.Value, ct).ConfigureAwait(false);
        var direction = current is null ? StoragePoolConstants.DirectionIn : StoragePoolConstants.DirectionMove;
        var origin = current ?? targetId;
        await EnsureNoConflictingTransferAsync(project["_id"].AsObjectId, origin, direction,
            current is null ? null : targetId, ct).ConfigureAwait(false);
        var transfer = await engine.PrepareAsync(project["_id"].AsObjectId, origin, direction,
            current is null ? null : targetId, ct).ConfigureAwait(false);
        MaybeDispatch(transfer);
        return StoragePoolTransferView.ToApi(transfer, project,
            await SourceAsync(origin, ct).ConfigureAwait(false), current is null ? null : target);
    }

    public async Task<BsonDocument> LockAsync(string projectId, JsonElement data, CancellationToken ct)
    {
        var project = await ProjectAsync(projectId, ct).ConfigureAwait(false);
        var locked = StoragePoolJson.Has(data, "locked") ? StoragePoolJson.RequiredBoolean(data, "locked") : true;
        if (locked && await transfers.HasActiveAsync(project["_id"].AsObjectId, ct).ConfigureAwait(false))
            throw new ApiError("StoragePoolNotAllowed", 5, "项目有进行中的迁移，不能锁定", 409);
        await Projects.UpdateOneAsync(new BsonDocument("_id", project["_id"]),
            new BsonDocument("$set", new BsonDocument("splk", locked)), cancellationToken: ct).ConfigureAwait(false);
        project["splk"] = locked;
        var source = StoragePoolBson.ObjectId(project, "csid") is { } sourceId
            ? await SourceAsync(sourceId, ct).ConfigureAwait(false)
            : null;
        return ProjectView(project, source);
    }

    public async Task<BsonDocument> StatsAsync(CancellationToken ct)
    {
        var sourceCount = await Sources.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct)
            .ConfigureAwait(false);
        var enabled = await Sources.CountDocumentsAsync(new BsonDocument("e", true), cancellationToken: ct)
            .ConfigureAwait(false);
        var migrated = await Projects.CountDocumentsAsync(new BsonDocument("csid", new BsonDocument("$ne", BsonNull.Value)),
            cancellationToken: ct).ConfigureAwait(false);
        var active = await TransferRows.CountDocumentsAsync(new BsonDocument
        {
            ["st"] = new BsonDocument("$in", new BsonArray
                { StoragePoolConstants.Pending, StoragePoolConstants.Copying, StoragePoolConstants.Verified, StoragePoolConstants.Switched }),
        }, cancellationToken: ct).ConfigureAwait(false);
        return new BsonDocument
        {
            ["sources"] = sourceCount,
            ["enabled_sources"] = enabled,
            ["migrated_projects"] = migrated,
            ["active_transfers"] = active,
        };
    }

    public async Task<BsonDocument> UsageAsync(CancellationToken ct)
    {
        var sourceRows = await Sources.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(ct).ConfigureAwait(false);
        var sourceNames = sourceRows.Where(row => row.TryGetValue("_id", out _))
            .ToDictionary(row => row["_id"].AsObjectId, row => StoragePoolBson.String(row, "n"));
        var bySource = new Dictionary<string, (long Bytes, int Projects)>(StringComparer.Ordinal);
        var top = new List<(ObjectId Id, string Name, long Bytes)>();
        long projects = 0, fileSizeKb = 0, coverBytes = 0, resampleBytes = 0, outputBytes = 0;
        long pendingProjects = 0, pendingFileSizeKb = 0, migratedProjects = 0;
        using (var cursor = await Projects.Find(FilterDefinition<BsonDocument>.Empty).ToCursorAsync(ct)
                   .ConfigureAwait(false))
        {
            while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
            {
                foreach (var project in cursor.Current)
                {
                    projects++;
                    var fs = StoragePoolBson.Long(project, "fs");
                    fileSizeKb += fs;
                    coverBytes += StoragePoolBson.Long(project, "cover_bytes");
                    resampleBytes += StoragePoolBson.Long(project, "resample_bytes");
                    outputBytes += StoragePoolBson.Long(project, "ob");
                    var sourceId = StoragePoolBson.ObjectId(project, "csid");
                    var key = sourceId?.ToString() ?? "";
                    bySource.TryGetValue(key, out var current);
                    bySource[key] = (current.Bytes + fs * 1024, current.Projects + 1);
                    if (sourceId is null)
                    {
                        if (fs > 0)
                        {
                            pendingProjects++;
                            pendingFileSizeKb += fs;
                        }
                    }
                    else
                    {
                        migratedProjects++;
                    }
                    if (fs > 0)
                        top.Add((project["_id"].AsObjectId, StoragePoolBson.String(project, "n"), fs * 1024));
                }
            }
        }
        var sourceArray = new BsonArray(bySource.Select(pair => new BsonDocument
        {
            ["source_id"] = pair.Key.Length == 0 ? BsonNull.Value : (BsonValue)pair.Key,
            ["name"] = pair.Key.Length == 0 ? "" : sourceNames.GetValueOrDefault(ObjectId.Parse(pair.Key), ""),
            ["bytes"] = pair.Value.Bytes,
            ["projects"] = pair.Value.Projects,
        }).OrderByDescending(row => row["bytes"].ToInt64()).Select(row => (BsonValue)row));
        var topArray = new BsonArray(top.OrderByDescending(item => item.Bytes).Take(10).Select(item => (BsonValue)new BsonDocument
        {
            ["id"] = item.Id,
            ["name"] = item.Name,
            ["bytes"] = item.Bytes,
        }));
        return new BsonDocument
        {
            ["total"] = new BsonDocument
            {
                ["projects"] = projects,
                ["bytes"] = fileSizeKb * 1024,
                ["cover_bytes"] = coverBytes,
                ["resample_bytes"] = resampleBytes,
                ["output_bytes"] = outputBytes,
            },
            ["pending"] = new BsonDocument
            {
                ["projects"] = pendingProjects,
                ["bytes"] = pendingFileSizeKb * 1024,
            },
            ["migrated_projects"] = migratedProjects,
            ["by_source"] = sourceArray,
            ["top_projects"] = topArray,
        };
    }

    private async Task EnsureNoConflictingTransferAsync(ObjectId projectId, ObjectId sourceId, int direction,
        ObjectId? targetId, CancellationToken ct)
    {
        var activeFilter = new BsonDocument
        {
            ["p"] = projectId,
            ["st"] = new BsonDocument { ["$gte"] = 0, ["$lt"] = 4 },
        };
        var active = await TransferRows.Find(activeFilter).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (active is null) return;
        if (StoragePoolBson.ObjectId(active, "s") == sourceId &&
            StoragePoolBson.Int(active, "dir") == direction &&
            StoragePoolBson.ObjectId(active, "ts") == targetId)
            return;
        throw new ApiError("StoragePoolTransferInProgress", 7, "项目已有进行中的迁移任务", 409);
    }

    private async Task<BsonDocument> ProjectAsync(string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out var key))
            throw new ApiError("ProjectNotExistError", 1001, "项目不存在", 404);
        return await Projects.Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new ApiError("ProjectNotExistError", 1001, "项目不存在", 404);
    }

    private static void RequireUnlocked(BsonDocument project)
    {
        if (StoragePoolBson.Bool(project, "splk"))
            throw new ApiError("StoragePoolNotAllowed", 5, "项目存储位置已锁定", 409);
    }

    private async Task RequireEnabledSourceAsync(ObjectId sourceId, CancellationToken ct)
    {
        var source = await SourceAsync(sourceId, ct).ConfigureAwait(false);
        if (!StoragePoolBson.Bool(source, "e", true))
            throw new ApiError("StoragePoolNotAllowed", 5, "存储来源已停用", 409);
    }

    private async Task<BsonDocument> SourceAsync(ObjectId id, CancellationToken ct) =>
        await Sources.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct).ConfigureAwait(false)
        ?? throw StoragePoolSourceService.Missing();

    private static ObjectId RequiredObjectId(JsonElement data, string field)
    {
        var value = StoragePoolJson.RequiredString(data, field, 64);
        if (!ObjectId.TryParse(value, out var parsed)) throw ApiError.Field(field, "格式不正确");
        return parsed;
    }

    private void MaybeDispatch(BsonDocument transfer)
    {
        var active = StoragePoolBson.Int(transfer, "st") is
            StoragePoolConstants.Pending or StoragePoolConstants.Copying or
            StoragePoolConstants.Verified or StoragePoolConstants.Switched;
        if (active && transfer.TryGetValue("_id", out var id) && id.IsObjectId)
            transfers.Dispatch(id.AsObjectId);
    }

    private static BsonDocument ProjectView(BsonDocument project, BsonDocument? source) => new()
    {
        ["id"] = project["_id"].ToString(),
        ["name"] = StoragePoolBson.String(project, "n"),
        ["storage_source_id"] = StoragePoolBson.ObjectId(project, "csid") is { } csid
            ? (BsonValue)csid.ToString()
            : BsonNull.Value,
        ["storage_source_switched_time"] = StoragePoolBson.Iso(project, "csw"),
        ["storage_pool_locked"] = StoragePoolBson.Bool(project, "splk"),
        ["file_size"] = StoragePoolBson.Long(project, "fs"),
        ["cover_bytes"] = StoragePoolBson.Long(project, "cover_bytes"),
        ["resample_bytes"] = StoragePoolBson.Long(project, "resample_bytes"),
        ["output_bytes"] = StoragePoolBson.Long(project, "ob"),
        ["source"] = source is null
            ? BsonNull.Value
            : new BsonDocument
            {
                ["id"] = source["_id"].ToString(),
                ["name"] = StoragePoolBson.String(source, "n"),
            },
    };
}
