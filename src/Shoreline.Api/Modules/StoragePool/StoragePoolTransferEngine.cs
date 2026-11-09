using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Configuration;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Modules.StoragePool;

public sealed record StoragePoolManifestItem(
    string Key,
    string Kind,
    long Size = 0,
    string ETag = "",
    ObjectId? FileId = null,
    ObjectId? OutputId = null);

public sealed class StoragePoolTransferEngine
{
    private readonly MongoStore store;
    private readonly StorageBackendFactory factory;
    private readonly ObjectStorage defaultStorage;
    private readonly ProjectLabelLock labels;
    private readonly StoragePoolLockService locks;
    private readonly StoragePoolPrefixes prefixes;
    private readonly IConfiguration configuration;
    private readonly TimeProvider clock;
    private readonly Func<StorageBackendDescriptor, StorageBackendLease>? leaseFactory;

    public StoragePoolTransferEngine(
        MongoStore store,
        StorageBackendFactory factory,
        ObjectStorage defaultStorage,
        ProjectLabelLock labels,
        StoragePoolLockService locks,
        IConfiguration configuration,
        TimeProvider clock,
        Func<StorageBackendDescriptor, StorageBackendLease>? leaseFactory = null)
    {
        this.store = store;
        this.factory = factory;
        this.defaultStorage = defaultStorage;
        this.labels = labels;
        this.locks = locks;
        this.configuration = configuration;
        this.clock = clock;
        this.leaseFactory = leaseFactory;
        prefixes = StoragePoolPrefixes.From(configuration);
    }

    private IMongoCollection<BsonDocument> Transfers => store.Collection(StoragePoolConstants.TransferCollection);
    private IMongoCollection<BsonDocument> Objects => store.Collection(StoragePoolConstants.ObjectCollection);
    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private IMongoCollection<BsonDocument> Files => store.Collection("file");
    private IMongoCollection<BsonDocument> Outputs => store.Collection("output");
    private IMongoCollection<BsonDocument> SourceRows => store.Collection(StoragePoolConstants.SourceCollection);
    private IMongoCollection<BsonDocument> RunLocks => store.Collection(StoragePoolConstants.RunLockCollection);

    public async Task<BsonDocument> PrepareAsync(ObjectId projectId, ObjectId sourceId, int direction,
        ObjectId? targetSourceId, CancellationToken ct)
    {
        var project = await Projects.Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new ApiError("ProjectNotExistError", 1001, "项目不存在", 404);
        var source = await SourceRows.Find(new BsonDocument("_id", sourceId)).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw StoragePoolSourceService.Missing();
        if (!StoragePoolBson.Bool(source, "e", true))
            throw new ApiError("StoragePoolNotAllowed", 5, "存储来源已停用", 409);
        if (direction == StoragePoolConstants.DirectionMove)
        {
            if (targetSourceId is null)
                throw new ApiError("StoragePoolNotAllowed", 5, "移动任务缺少目标来源", 409);
            var target = await SourceRows.Find(new BsonDocument("_id", targetSourceId.Value)).FirstOrDefaultAsync(ct)
                .ConfigureAwait(false) ?? throw StoragePoolSourceService.Missing();
            if (!StoragePoolBson.Bool(target, "e", true))
                throw new ApiError("StoragePoolNotAllowed", 5, "目标存储来源已停用", 409);
        }

        var existing = await Transfers.Find(ActiveProjectFilter(projectId)).FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (MatchesRequest(existing, sourceId, direction, targetSourceId)) return existing;
            throw TransferInProgress();
        }

        var transferId = ObjectId.GenerateNewId();
        var manifest = await BuildManifestAsync(projectId, ct).ConfigureAwait(false);
        if (manifest.Count > 0)
        {
            try
            {
                await InsertManifestRowsAsync(transferId, manifest, ct).ConfigureAwait(false);
            }
            catch
            {
                await BestEffortDeleteRowsAsync(transferId).ConfigureAwait(false);
                throw;
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var transfer = new BsonDocument
        {
            ["_id"] = transferId,
            ["s"] = sourceId,
            ["ts"] = targetSourceId is null ? BsonNull.Value : targetSourceId.Value,
            ["p"] = projectId,
            ["st"] = StoragePoolConstants.Pending,
            ["dir"] = direction,
            ["set"] = StoragePoolBson.DateOrNull(project, "m_e"),
            ["to"] = manifest.Count,
            ["co"] = 0,
            ["processed"] = 0,
            ["skipped"] = 0,
            ["verified"] = 0,
            ["deleted"] = 0,
            ["tb"] = StoragePoolBson.Long(project, "fs") * 1024,
            ["cb"] = 0,
            ["bd"] = new BsonDocument { ["ob"] = 0L, ["ov"] = 0L, ["oc"] = 0 },
            ["npc"] = 0,
            ["stt"] = BsonNull.Value,
            ["ftt"] = BsonNull.Value,
            ["le"] = "",
            ["ct"] = now,
        };
        try
        {
            await Transfers.InsertOneAsync(transfer, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (MongoWriteException error) when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            await BestEffortDeleteRowsAsync(transferId).ConfigureAwait(false);
            var winner = await Transfers.Find(ActiveProjectFilter(projectId)).FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (winner is not null && MatchesRequest(winner, sourceId, direction, targetSourceId)) return winner;
            throw TransferInProgress();
        }
        catch
        {
            await BestEffortDeleteRowsAsync(transferId).ConfigureAwait(false);
            throw;
        }
        return transfer;
    }

    public async Task<List<StoragePoolManifestItem>> BuildManifestAsync(ObjectId projectId, CancellationToken ct)
    {
        var files = await Files.Find(new BsonDocument
            {
                ["p"] = projectId,
                ["t"] = 2,
                ["sa"] = new BsonDocument("$ne", ""),
            }).Sort(new BsonDocument("_id", 1)).ToListAsync(ct).ConfigureAwait(false);
        var outputs = await Outputs.Find(new BsonDocument
            {
                ["p"] = projectId,
                ["file_name"] = new BsonDocument("$gt", ""),
            }).Sort(new BsonDocument("_id", 1)).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<StoragePoolManifestItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var saveName = StoragePoolBson.String(file, "sa");
            if (saveName.Length == 0) continue;
            var original = prefixes.FilePrefix + saveName;
            if (seen.Add(original))
                result.Add(new StoragePoolManifestItem(original, StoragePoolConstants.KindOriginal,
                    StoragePoolBson.Long(file, "fs") * 1024, "", StoragePoolBson.ObjectId(file, "_id")));
            var stem = saveName.Contains('.') ? saveName[..saveName.LastIndexOf('.')] : saveName;
            var resample = prefixes.FilePrefix + prefixes.ResampleName + "-" + stem + ".webp";
            if (seen.Add(resample))
                result.Add(new StoragePoolManifestItem(resample, StoragePoolConstants.KindResample,
                    0, "", StoragePoolBson.ObjectId(file, "_id")));
        }
        foreach (var output in outputs)
        {
            var id = StoragePoolBson.ObjectId(output, "_id");
            var fileName = StoragePoolBson.String(output, "file_name");
            if (id is null || fileName.Length == 0) continue;
            var key = prefixes.OutputPrefix + id.Value + "/" + fileName;
            if (seen.Add(key))
                result.Add(new StoragePoolManifestItem(key, StoragePoolConstants.KindOutput,
                    StoragePoolBson.Long(output, "sb"), "", OutputId: id));
        }
        return result;
    }

    private async Task InsertManifestRowsAsync(ObjectId transferId, List<StoragePoolManifestItem> manifest,
        CancellationToken ct)
    {
        for (var offset = 0; offset < manifest.Count; offset += 1000)
        {
            var batch = manifest.Skip(offset).Take(1000).Select(item => new BsonDocument
            {
                ["t"] = transferId,
                ["sk"] = item.Key,
                ["dk"] = item.Key,
                ["kd"] = item.Kind,
                ["sz"] = item.Size,
                ["et"] = item.ETag,
                ["st"] = StoragePoolConstants.ObjectPending,
                ["at"] = 0,
                ["le"] = "",
            }).ToList();
            await Objects.InsertManyAsync(batch, cancellationToken: ct).ConfigureAwait(false);
        }
    }

    private static BsonDocument ActiveProjectFilter(ObjectId projectId) => new()
    {
        ["p"] = projectId,
        ["st"] = new BsonDocument { ["$gte"] = 0, ["$lt"] = 4 },
    };

    private static bool MatchesRequest(BsonDocument transfer, ObjectId sourceId, int direction,
        ObjectId? targetSourceId) =>
        StoragePoolBson.ObjectId(transfer, "s") == sourceId &&
        StoragePoolBson.Int(transfer, "dir") == direction &&
        StoragePoolBson.ObjectId(transfer, "ts") == targetSourceId;

    private static ApiError TransferInProgress() =>
        new("StoragePoolTransferInProgress", 7, "项目已有进行中的迁移任务", 409);

    private async Task BestEffortDeleteRowsAsync(ObjectId transferId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Objects.DeleteManyAsync(new BsonDocument("t", transferId), timeout.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // Orphan rows without a transfer are inert and are never routed.
        }
    }

    public async Task RunAsync(ObjectId transferId, CancellationToken ct)
    {
        var owner = Guid.NewGuid().ToString("N");
        try
        {
            await RunLocks.InsertOneAsync(new BsonDocument
            {
                ["_id"] = transferId,
                ["o"] = owner,
                ["ct"] = clock.GetUtcNow().UtcDateTime,
            }, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (MongoWriteException error) when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return;
        }
        catch
        {
            // The driver may have committed the insert before reporting an
            // unknown result. The owner-CAS cleanup is harmless when it did not.
            await ReleaseRunLockBoundedAsync(transferId, owner).ConfigureAwait(false);
            throw;
        }
        try
        {
            await ExecuteAsync(transferId, owner, ct).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseRunLockBoundedAsync(transferId, owner).ConfigureAwait(false);
        }
    }

    private async Task ReleaseRunLockBoundedAsync(ObjectId transferId, string owner)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await RunLocks.DeleteOneAsync(new BsonDocument { ["_id"] = transferId, ["o"] = owner },
                timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Do not mask the worker's original failure.
        }
    }

    internal async Task ExecuteAsync(ObjectId transferId, string owner, CancellationToken ct)
    {
        for (var round = 0; round < 1000 && !ct.IsCancellationRequested; round++)
        {
            var transfer = await LoadTransferAsync(transferId, ct).ConfigureAwait(false);
            if (transfer is null) return;
            var status = StoragePoolBson.Int(transfer, "st");
            if (status is StoragePoolConstants.Done or StoragePoolConstants.Cancelled or
                StoragePoolConstants.Failed or StoragePoolConstants.Aborted) return;

            if (status == StoragePoolConstants.Pending)
            {
                var claimed = await Transfers.UpdateOneAsync(
                    new BsonDocument { ["_id"] = transferId, ["st"] = StoragePoolConstants.Pending },
                    new BsonDocument("$set", new BsonDocument
                    {
                        ["st"] = StoragePoolConstants.Copying,
                        ["stt"] = clock.GetUtcNow().UtcDateTime,
                    }), cancellationToken: ct).ConfigureAwait(false);
                if (claimed.ModifiedCount == 0) return;
                transfer = await LoadTransferAsync(transferId, ct).ConfigureAwait(false);
                if (transfer is null) return;
                status = StoragePoolBson.Int(transfer, "st");
            }

            var projectId = StoragePoolBson.ObjectId(transfer, "p");
            if (projectId is null)
            {
                await MarkAbortedAsync(transferId, "项目不存在", ct);
                return;
            }
            var project = await Projects.Find(new BsonDocument("_id", projectId.Value)).FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (project is null || StoragePoolBson.Int(project, "st") != 0 || StoragePoolBson.Bool(project, "cip"))
            {
                await MarkAbortedAsync(transferId, "项目状态不允许迁移", ct);
                return;
            }

            var sourceId = StoragePoolBson.ObjectId(transfer, "s");
            var targetId = StoragePoolBson.ObjectId(transfer, "ts");
            if (sourceId is null)
            {
                await MarkAbortedAsync(transferId, "存储来源不存在", ct);
                return;
            }
            var source = await SourceRows.Find(new BsonDocument("_id", sourceId.Value)).FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (source is null || !StoragePoolBson.Bool(source, "e", true))
            {
                await MarkAbortedAsync(transferId, "存储来源不存在或已停用", ct);
                return;
            }
            var target = targetId is null
                ? null
                : await SourceRows.Find(new BsonDocument("_id", targetId.Value)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            var direction = StoragePoolBson.Int(transfer, "dir");
            if (direction == StoragePoolConstants.DirectionMove && target is null)
            {
                await MarkAbortedAsync(transferId, "目标存储来源不存在", ct);
                return;
            }

            if (status == StoragePoolConstants.Copying)
            {
                var copy = await CopyBatchAsync(transfer, source, target, ct).ConfigureAwait(false);
                await RecomputeAsync(transferId, ct).ConfigureAwait(false);
                if (copy.Failed)
                {
                    await FailAsync(transferId, "对象复制失败", ct);
                    return;
                }
                var pending = await Objects.CountDocumentsAsync(new BsonDocument
                {
                    ["t"] = transferId,
                    ["st"] = StoragePoolConstants.ObjectPending,
                }, cancellationToken: ct).ConfigureAwait(false);
                if (pending > 0)
                {
                    if (copy.Moved)
                        await Transfers.UpdateOneAsync(new BsonDocument("_id", transferId),
                            new BsonDocument("$set", new BsonDocument("npc", 0)), cancellationToken: ct)
                            .ConfigureAwait(false);
                    else
                        await StallOrFailAsync(transferId, "等待继续复制", ct).ConfigureAwait(false);
                    return;
                }
                await Transfers.UpdateOneAsync(new BsonDocument
                {
                    ["_id"] = transferId,
                    ["st"] = StoragePoolConstants.Copying,
                }, new BsonDocument("$set", new BsonDocument("st", StoragePoolConstants.Verified)),
                    cancellationToken: ct).ConfigureAwait(false);
                await RecomputeAsync(transferId, ct).ConfigureAwait(false);
                transfer = await LoadTransferAsync(transferId, ct).ConfigureAwait(false);
                if (transfer is null) return;
                status = StoragePoolBson.Int(transfer, "st");
            }

            if (status == StoragePoolConstants.Verified)
            {
                project = await Projects.Find(new BsonDocument("_id", projectId.Value)).FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                if (project is null)
                {
                    await MarkAbortedAsync(transferId, "项目不存在", ct);
                    return;
                }
                var verified = await VerifyBatchAsync(transferId, source, target, ct).ConfigureAwait(false);
                await RecomputeAsync(transferId, ct).ConfigureAwait(false);
                if (!verified)
                {
                    await FailAsync(transferId, "对象校验失败", ct);
                    return;
                }
                var switchResult = await SwitchAsync(transfer, project, source, target, owner, ct).ConfigureAwait(false);
                if (switchResult == SwitchResult.Busy) return;
                if (switchResult == SwitchResult.Aborted)
                {
                    await MarkAbortedAsync(transferId, "项目位置或版本已变化", ct);
                    return;
                }
                transfer = await LoadTransferAsync(transferId, ct).ConfigureAwait(false);
                if (transfer is null) return;
                status = StoragePoolBson.Int(transfer, "st");
            }

            if (status == StoragePoolConstants.Switched)
            {
                var complete = await DeleteSourceAsync(transfer, source, ct).ConfigureAwait(false);
                await RecomputeAsync(transferId, ct).ConfigureAwait(false);
                if (!complete)
                {
                    await StallOrFailAsync(transferId, "源端仍有残留对象", ct, doneWithWarning: true).ConfigureAwait(false);
                    return;
                }
                await RefreshHasDataAsync(sourceId.Value, targetId, ct).ConfigureAwait(false);
                await Transfers.UpdateOneAsync(new BsonDocument("_id", transferId),
                    new BsonDocument("$set", new BsonDocument
                    {
                        ["st"] = StoragePoolConstants.Done,
                        ["ftt"] = clock.GetUtcNow().UtcDateTime,
                        ["le"] = "",
                    }), cancellationToken: ct).ConfigureAwait(false);
                return;
            }
        }
        await StallOrFailAsync(transferId, "迁移未完成", ct).ConfigureAwait(false);
    }

    private readonly record struct CopyResult(bool Failed, bool Moved);

    private async Task<CopyResult> CopyBatchAsync(BsonDocument transfer, BsonDocument source, BsonDocument? target,
        CancellationToken ct)
    {
        var transferId = transfer["_id"].AsObjectId;
        var direction = StoragePoolBson.Int(transfer, "dir");
        var limit = Math.Clamp(StoragePoolBson.Int(source, "mo", 2000), 1, 100000);
        if (target is not null)
            limit = Math.Min(limit, Math.Clamp(StoragePoolBson.Int(target, "mo", 2000), 1, 100000));
        var sourceDescriptor = direction == StoragePoolConstants.DirectionIn
            ? DefaultDescriptor()
            : StoragePoolSourceService.Descriptor(source);
        var targetDescriptor = direction switch
        {
            StoragePoolConstants.DirectionIn => StoragePoolSourceService.Descriptor(source),
            StoragePoolConstants.DirectionOut => DefaultDescriptor(),
            _ => target is null ? null : StoragePoolSourceService.Descriptor(target),
        };
        await using var origin = await GetLeaseAsync(direction != StoragePoolConstants.DirectionIn, source, ct)
            .ConfigureAwait(false);
        var destinationIsPool = direction != StoragePoolConstants.DirectionOut;
        var destinationDocument = direction == StoragePoolConstants.DirectionOut ? source : target ?? source;
        await using var destination = await GetLeaseAsync(destinationIsPool, destinationDocument, ct)
            .ConfigureAwait(false);
        var anyFailed = false;
        var moved = false;
        var processed = 0;
        using var cursor = await Objects.Find(new BsonDocument
            {
                ["t"] = transferId,
                ["st"] = StoragePoolConstants.ObjectPending,
            }).Sort(new BsonDocument("_id", 1)).ToCursorAsync(ct).ConfigureAwait(false);
        while (processed < limit && await cursor.MoveNextAsync(ct).ConfigureAwait(false))
        {
            foreach (var row in cursor.Current)
            {
                if (processed >= limit) break;
                processed++;
                ct.ThrowIfCancellationRequested();
                var key = StoragePoolBson.String(row, "sk");
                var kind = StoragePoolBson.String(row, "kd");
                try
                {
                    var head = await origin.Backend.HeadAsync(key, ct).ConfigureAwait(false);
                    if (head is null)
                    {
                        if (kind == StoragePoolConstants.KindResample)
                            await MarkObjectAsync(row, StoragePoolConstants.ObjectSkipped, "重采样文件不存在", ct)
                                .ConfigureAwait(false);
                        else
                        {
                            anyFailed = true;
                            await MarkObjectAsync(row, StoragePoolConstants.ObjectFailed, "源对象不存在", ct, true)
                                .ConfigureAwait(false);
                        }
                        continue;
                    }
                    var copied = false;
                    if (sourceDescriptor is not null && targetDescriptor is not null)
                    {
                        copied = await factory.TryCopyServerSideAsync(sourceDescriptor, targetDescriptor, key, key, ct)
                            .ConfigureAwait(false);
                    }
                    if (!copied)
                    {
                        using var download = await origin.Backend.GetAsync(key, ct).ConfigureAwait(false);
                        using var counted = new CountingReadStream(download.Content);
                        await destination.Backend.PutAsync(key, counted, download.ContentType, ct)
                            .ConfigureAwait(false);
                        if (counted.BytesRead != head.Size)
                            throw new InvalidOperationException("源对象读取不完整");
                    }
                    var destinationHead = await destination.Backend.HeadAsync(key, ct).ConfigureAwait(false);
                    if (destinationHead is null) throw new InvalidOperationException("目标对象缺失");
                    // A short write can return success without an SDK exception.
                    // Source HEAD size is the authority; 0-byte objects must be
                    // compared too. ETag is stored only as a diagnostic value
                    // because multipart/cross-provider ETags are not comparable.
                    if (destinationHead.Size != head.Size)
                        throw new InvalidOperationException("目标对象大小与源对象不一致");
                    var claimed = await Objects.UpdateOneAsync(new BsonDocument
                        {
                            ["_id"] = row["_id"],
                            ["st"] = StoragePoolConstants.ObjectPending,
                        },
                        new BsonDocument("$set", new BsonDocument
                        {
                            ["st"] = StoragePoolConstants.ObjectCopied,
                            ["sz"] = head.Size,
                            ["et"] = destinationHead.ETag ?? "",
                            ["le"] = "",
                        }), cancellationToken: ct).ConfigureAwait(false);
                    if (claimed.ModifiedCount > 0) moved = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    if (kind == StoragePoolConstants.KindResample)
                        await MarkObjectAsync(row, StoragePoolConstants.ObjectSkipped, Sanitize(error), ct)
                            .ConfigureAwait(false);
                    else
                    {
                        anyFailed = true;
                        await MarkObjectAsync(row, StoragePoolConstants.ObjectFailed, Sanitize(error), ct, true)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
        return new CopyResult(anyFailed, moved);
    }

    private async Task<bool> VerifyBatchAsync(ObjectId transferId, BsonDocument source, BsonDocument? target,
        CancellationToken ct)
    {
        var transfer = await LoadTransferAsync(transferId, ct).ConfigureAwait(false);
        var direction = transfer is null ? StoragePoolConstants.DirectionIn : StoragePoolBson.Int(transfer, "dir");
        var destinationIsPool = direction != StoragePoolConstants.DirectionOut;
        var destinationDocument = direction == StoragePoolConstants.DirectionOut ? source : target ?? source;
        await using var destination = await GetLeaseAsync(destinationIsPool, destinationDocument, ct)
            .ConfigureAwait(false);
        var allVerified = true;
        using var cursor = await Objects.Find(new BsonDocument
            {
                ["t"] = transferId,
                ["st"] = StoragePoolConstants.ObjectCopied,
            }).ToCursorAsync(ct).ConfigureAwait(false);
        while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
        {
            foreach (var row in cursor.Current)
            {
                var key = StoragePoolBson.String(row, "sk");
                var head = await destination.Backend.HeadAsync(key, ct).ConfigureAwait(false);
                if (head is null)
                {
                    allVerified = false;
                    await MarkObjectAsync(row, StoragePoolConstants.ObjectFailed, "目标对象缺失", ct, true)
                        .ConfigureAwait(false);
                    continue;
                }
                // `sz` is the verified source size/actual stream count, not a
                // destination-derived value. Check zero-byte objects too.
                // `et` is diagnostic only: multipart/cross-provider ETags are
                // not required to be byte-comparable.
                if (head.Size != StoragePoolBson.Long(row, "sz"))
                {
                    allVerified = false;
                    await MarkObjectAsync(row, StoragePoolConstants.ObjectFailed, "目标对象大小不一致", ct, true)
                        .ConfigureAwait(false);
                    continue;
                }
                await MarkObjectAsync(row, StoragePoolConstants.ObjectVerified, "", ct).ConfigureAwait(false);
            }
        }
        return allVerified;
    }

    private enum SwitchResult { Switched, Busy, Aborted }

    private async Task<SwitchResult> SwitchAsync(BsonDocument transfer, BsonDocument project, BsonDocument source,
        BsonDocument? target, string owner, CancellationToken ct)
    {
        _ = owner;
        var projectId = project["_id"].AsObjectId;
        var transferId = transfer["_id"].AsObjectId;
        var direction = StoragePoolBson.Int(transfer, "dir");
        var sourceId = source["_id"].AsObjectId;
        var targetId = target?["_id"].AsObjectId;
        LabelSession session;
        try
        {
            session = await labels.EnterAsync(new LabelRequestContext(), projectId, false, null, ct)
                .ConfigureAwait(false);
        }
        catch (ApiError error) when (error.Status == 409)
        {
            return SwitchResult.Busy;
        }

        await using (session)
        {
            var current = await Projects.Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (current is null || StoragePoolBson.Int(current, "st") != 0 || StoragePoolBson.Bool(current, "cip"))
                return SwitchResult.Aborted;
            var snapshot = StoragePoolBson.Date(transfer, "set");
            var currentEdit = StoragePoolBson.Date(current, "m_e");
            if ((snapshot is null) != (currentEdit is null) || snapshot?.Ticks != currentEdit?.Ticks)
                return SwitchResult.Aborted;

            var manifest = await BuildManifestAsync(projectId, ct).ConfigureAwait(false);
            var existingKeys = await Objects.Find(new BsonDocument("t", transferId))
                .Project(new BsonDocument("sk", 1)).ToListAsync(ct).ConfigureAwait(false);
            var known = existingKeys.Select(row => StoragePoolBson.String(row, "sk")).ToHashSet(StringComparer.Ordinal);
            if (manifest.Any(item => !known.Contains(item.Key))) return SwitchResult.Aborted;

            var lockOwner = "transfer:" + transferId;
            if (!await locks.AcquireAsync(projectId, transferId, lockOwner, ct: ct).ConfigureAwait(false))
                return SwitchResult.Busy;

            try
            {
                current = await Projects.Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                if (current is null || StoragePoolBson.Int(current, "st") != 0 || StoragePoolBson.Bool(current, "cip"))
                    return SwitchResult.Aborted;
                var currentSource = StoragePoolBson.ObjectId(current, "csid");
                var accepted = direction switch
                {
                    StoragePoolConstants.DirectionIn => currentSource is null || currentSource == sourceId,
                    StoragePoolConstants.DirectionOut => currentSource is null || currentSource == sourceId,
                    StoragePoolConstants.DirectionMove => currentSource == sourceId || currentSource == targetId,
                    _ => false,
                };
                if (!accepted) return SwitchResult.Aborted;
                var destination = direction switch
                {
                    StoragePoolConstants.DirectionIn => (ObjectId?)sourceId,
                    StoragePoolConstants.DirectionOut => null,
                    _ => targetId,
                };
                await Projects.UpdateOneAsync(new BsonDocument("_id", projectId),
                    new BsonDocument("$set", new BsonDocument
                    {
                        ["csid"] = destination is null ? BsonNull.Value : destination.Value,
                        ["csw"] = clock.GetUtcNow().UtcDateTime,
                    }), cancellationToken: ct).ConfigureAwait(false);
                // Keep pointer, metrics, and status in the same project label
                // lock. If a post-pointer write fails, the next run sees an
                // already-switched destination and resumes without copying.
                await ApplyMetricsAsync(transfer, ct).ConfigureAwait(false);
                await Transfers.UpdateOneAsync(new BsonDocument("_id", transferId),
                    new BsonDocument("$set", new BsonDocument("st", StoragePoolConstants.Switched)),
                    cancellationToken: ct).ConfigureAwait(false);
                return SwitchResult.Switched;
            }
            finally
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await locks.ReleaseAsync(projectId, transferId, lockOwner, timeout.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The 1800s lease expires without blocking correctness.
                }
            }
        }
    }

    private async Task<bool> DeleteSourceAsync(BsonDocument transfer, BsonDocument source, CancellationToken ct)
    {
        var transferId = transfer["_id"].AsObjectId;
        var direction = StoragePoolBson.Int(transfer, "dir");
        await using var origin = await GetLeaseAsync(direction != StoragePoolConstants.DirectionIn, source, ct)
            .ConfigureAwait(false);
        while (true)
        {
            var rows = await Objects.Find(new BsonDocument
                {
                    ["t"] = transferId,
                    ["st"] = StoragePoolConstants.ObjectVerified,
                }).Sort(new BsonDocument("_id", 1)).Limit(StoragePoolConstants.DeleteBatchSize).ToListAsync(ct)
                .ConfigureAwait(false);
            if (rows.Count == 0) return true;

            var failed = false;
            foreach (var row in rows)
            {
                var key = StoragePoolBson.String(row, "sk");
                try
                {
                    await origin.Backend.DeleteAsync(key, ct).ConfigureAwait(false);
                    await MarkObjectAsync(row, StoragePoolConstants.ObjectSourceDeleted, "", ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    failed = true;
                    await Objects.UpdateOneAsync(new BsonDocument("_id", row["_id"]),
                        new BsonDocument("$set", new BsonDocument("le", Sanitize(error))), cancellationToken: ct)
                        .ConfigureAwait(false);
                }
            }
            if (failed) return false;
        }
    }

    private async Task ApplyMetricsAsync(BsonDocument transfer, CancellationToken ct)
    {
        var transferId = transfer["_id"].AsObjectId;
        var byName = new Dictionary<string, ObjectId>(StringComparer.Ordinal);
        var byStem = new Dictionary<string, ObjectId>(StringComparer.Ordinal);
        using (var imageCursor = await Files.Find(new BsonDocument { ["p"] = transfer["p"], ["t"] = 2 })
                   .ToCursorAsync(ct).ConfigureAwait(false))
        {
            while (await imageCursor.MoveNextAsync(ct).ConfigureAwait(false))
            {
                foreach (var image in imageCursor.Current)
                {
                    if (StoragePoolBson.ObjectId(image, "_id") is not { } imageId) continue;
                    var saveName = StoragePoolBson.String(image, "sa");
                    if (saveName.Length == 0) continue;
                    byName[saveName] = imageId;
                    byStem[Path.GetFileNameWithoutExtension(saveName)] = imageId;
                }
            }
        }

        using var rowCursor = await Objects.Find(new BsonDocument
        {
            ["t"] = transferId,
            ["st"] = new BsonDocument("$in", new BsonArray
                { StoragePoolConstants.ObjectVerified, StoragePoolConstants.ObjectSourceDeleted }),
        }).ToCursorAsync(ct).ConfigureAwait(false);
        while (await rowCursor.MoveNextAsync(ct).ConfigureAwait(false))
        {
            foreach (var row in rowCursor.Current)
            {
                var key = StoragePoolBson.String(row, "sk");
                var size = StoragePoolBson.Long(row, "sz");
                if (key.StartsWith(prefixes.OutputPrefix, StringComparison.Ordinal))
                {
                    var rest = key[prefixes.OutputPrefix.Length..];
                    var slash = rest.IndexOf('/');
                    if (slash > 0 && ObjectId.TryParse(rest[..slash], out var outputId))
                        await Outputs.UpdateOneAsync(new BsonDocument("_id", outputId),
                            new BsonDocument("$set", new BsonDocument("sb", size)), cancellationToken: ct).ConfigureAwait(false);
                    continue;
                }
                var name = key.StartsWith(prefixes.FilePrefix, StringComparison.Ordinal)
                    ? key[prefixes.FilePrefix.Length..]
                    : key;
                if (name.StartsWith(prefixes.ResampleName + "-", StringComparison.Ordinal))
                {
                    var stem = name[(prefixes.ResampleName.Length + 1)..];
                    if (stem.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^5];
                    if (byStem.TryGetValue(stem, out var imageId))
                        await Files.UpdateOneAsync(new BsonDocument("_id", imageId),
                            new BsonDocument("$set", new BsonDocument("resample_bytes", size)), cancellationToken: ct)
                            .ConfigureAwait(false);
                }
                else if (byName.TryGetValue(name, out var imageId2))
                {
                    await Files.UpdateOneAsync(new BsonDocument("_id", imageId2),
                        new BsonDocument("$set", new BsonDocument("fs", (size + 1023) / 1024)), cancellationToken: ct)
                        .ConfigureAwait(false);
                }
            }
        }
        await RebuildStatisticsAsync(transfer["p"].AsObjectId, ct).ConfigureAwait(false);
    }

    private async Task RebuildStatisticsAsync(ObjectId projectId, CancellationToken ct)
    {
        long original = 0, cover = 0, resample = 0;
        using (var cursor = await Files.Find(new BsonDocument
                   {
                       ["p"] = projectId,
                       ["t"] = new BsonDocument("$ne", 1),
                       ["sa"] = new BsonDocument("$ne", ""),
                   }).ToCursorAsync(ct).ConfigureAwait(false))
        {
            while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
            {
                foreach (var file in cursor.Current)
                {
                    original += StoragePoolBson.Long(file, "fs") * 1024;
                    cover += StoragePoolBson.Long(file, "cover_bytes");
                    resample += StoragePoolBson.Long(file, "resample_bytes");
                }
            }
        }
        long outputBytes = 0;
        using (var cursor = await Outputs.Find(new BsonDocument
                   {
                       ["p"] = projectId,
                       ["file_name"] = new BsonDocument("$gt", ""),
                   }).ToCursorAsync(ct).ConfigureAwait(false))
        {
            while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
            {
                foreach (var output in cursor.Current)
                    outputBytes += StoragePoolBson.Long(output, "sb");
            }
        }
        var total = original + cover + resample + outputBytes;
        var kb = (long)Math.Round(total / 1024.0, MidpointRounding.ToEven);
        await Projects.UpdateOneAsync(new BsonDocument("_id", projectId), new BsonDocument("$set", new BsonDocument
        {
            ["fs"] = kb,
            ["cover_bytes"] = cover,
            ["resample_bytes"] = resample,
            ["ob"] = outputBytes,
        }), cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task RecomputeAsync(ObjectId transferId, CancellationToken ct)
    {
        long total = 0, pending = 0, copied = 0, verified = 0, deleted = 0, skipped = 0, failed = 0;
        long copiedBytes = 0, originBytes = 0, outputBytes = 0;
        using (var cursor = await Objects.Find(new BsonDocument("t", transferId)).ToCursorAsync(ct)
                   .ConfigureAwait(false))
        {
            while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
            {
                foreach (var row in cursor.Current)
                {
                    total++;
                    var objectStatus = StoragePoolBson.Int(row, "st");
                    if (objectStatus == StoragePoolConstants.ObjectPending) pending++;
                    if (objectStatus is StoragePoolConstants.ObjectCopied or
                        StoragePoolConstants.ObjectVerified or StoragePoolConstants.ObjectSourceDeleted) copied++;
                    if (objectStatus is StoragePoolConstants.ObjectVerified or
                        StoragePoolConstants.ObjectSourceDeleted) verified++;
                    if (objectStatus == StoragePoolConstants.ObjectSourceDeleted) deleted++;
                    if (objectStatus == StoragePoolConstants.ObjectSkipped) skipped++;
                    if (objectStatus == StoragePoolConstants.ObjectFailed) failed++;
                    if (objectStatus is StoragePoolConstants.ObjectCopied or
                        StoragePoolConstants.ObjectVerified or StoragePoolConstants.ObjectSourceDeleted)
                    {
                        var size = StoragePoolBson.Long(row, "sz");
                        copiedBytes += size;
                        var kind = StoragePoolBson.String(row, "kd");
                        if (kind == StoragePoolConstants.KindOutput || kind == StoragePoolConstants.KindText)
                            outputBytes += size;
                        else
                            originBytes += size;
                    }
                }
            }
        }
        var processed = total - pending;
        await Transfers.UpdateOneAsync(new BsonDocument("_id", transferId), new BsonDocument("$set", new BsonDocument
        {
            ["co"] = copied,
            ["processed"] = processed,
            ["skipped"] = skipped,
            ["verified"] = verified,
            ["deleted"] = deleted,
            ["cb"] = copiedBytes,
            ["bd"] = new BsonDocument
            {
                ["ob"] = originBytes,
                ["ov"] = outputBytes,
                ["oc"] = copied,
            },
            ["to"] = total,
            ["le"] = failed > 0 ? "部分对象复制失败" : "",
        }), cancellationToken: ct).ConfigureAwait(false);
    }

    public Task<BsonDocument?> GetTransferAsync(ObjectId transferId, CancellationToken ct) =>
        Transfers.Find(new BsonDocument("_id", transferId)).FirstOrDefaultAsync(ct)!;

    public async Task CancelAsync(ObjectId transferId, CancellationToken ct)
    {
        var result = await Transfers.UpdateOneAsync(
            new BsonDocument { ["_id"] = transferId, ["st"] = StoragePoolConstants.Pending },
            new BsonDocument("$set", new BsonDocument
            {
                ["st"] = StoragePoolConstants.Cancelled,
                ["ftt"] = clock.GetUtcNow().UtcDateTime,
                ["le"] = "管理员取消排队任务",
            }), cancellationToken: ct).ConfigureAwait(false);
        if (result.MatchedCount == 0)
            throw new ApiError("StoragePoolTransferInProgress", 7, "迁移任务不在排队状态", 409);
    }

    public async Task DeleteAsync(ObjectId transferId, CancellationToken ct)
    {
        var transfer = await GetTransferAsync(transferId, ct).ConfigureAwait(false);
        if (transfer is null) return;
        var status = StoragePoolBson.Int(transfer, "st");
        if (status is not (StoragePoolConstants.Cancelled or StoragePoolConstants.Done))
            throw new ApiError("StoragePoolTransferInProgress", 7, "只能删除已取消或已完成的任务", 409);
        if (status == StoragePoolConstants.Done && StoragePoolBson.String(transfer, "le").Length > 0)
            throw new ApiError("StoragePoolTransferInProgress", 7, "任务仍有残留告警，不能删除", 409);
        if (await RunLocks.Find(new BsonDocument("_id", transferId)).AnyAsync(ct).ConfigureAwait(false))
            throw new ApiError("StoragePoolTransferInProgress", 7, "worker 尚未退出", 409);
        await Objects.DeleteManyAsync(new BsonDocument("t", transferId), ct).ConfigureAwait(false);
        await Transfers.DeleteOneAsync(new BsonDocument("_id", transferId), ct).ConfigureAwait(false);
    }

    public async Task ResetForRetryAsync(ObjectId transferId, CancellationToken ct)
    {
        var transfer = await GetTransferAsync(transferId, ct).ConfigureAwait(false)
            ?? throw new ApiError("StoragePoolTransferNotFound", 1, "迁移任务不存在", 404);
        var status = StoragePoolBson.Int(transfer, "st");
        if (!StoragePoolConstants.IsRetryable(status))
            throw new ApiError("StoragePoolTransferInProgress", 7, "任务不可重试", 409);
        if (status == StoragePoolConstants.Aborted)
        {
            await Objects.DeleteManyAsync(new BsonDocument("t", transferId), ct).ConfigureAwait(false);
            var manifest = await BuildManifestAsync(transfer["p"].AsObjectId, ct).ConfigureAwait(false);
            if (manifest.Count > 0)
                await InsertManifestRowsAsync(transferId, manifest, ct).ConfigureAwait(false);
            var project = await Projects.Find(new BsonDocument("_id", transfer["p"])).FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            await Transfers.UpdateOneAsync(new BsonDocument("_id", transferId), new BsonDocument("$set", new BsonDocument
            {
                ["to"] = manifest.Count,
                ["co"] = 0,
                ["processed"] = 0,
                ["skipped"] = 0,
                ["verified"] = 0,
                ["deleted"] = 0,
                ["tb"] = project is null ? 0 : StoragePoolBson.Long(project, "fs") * 1024,
                ["cb"] = 0,
                ["bd"] = new BsonDocument { ["ob"] = 0L, ["ov"] = 0L, ["oc"] = 0 },
                ["set"] = project is null ? BsonNull.Value : StoragePoolBson.DateOrNull(project, "m_e"),
                ["npc"] = 0,
                ["stt"] = BsonNull.Value,
                ["ftt"] = BsonNull.Value,
                ["le"] = "",
            }), cancellationToken: ct).ConfigureAwait(false);
        }
        else
        {
            await Objects.UpdateManyAsync(new BsonDocument
            {
                ["t"] = transferId,
                ["st"] = StoragePoolConstants.ObjectFailed,
            }, new BsonDocument("$set", new BsonDocument
            {
                ["st"] = StoragePoolConstants.ObjectPending,
                ["le"] = "",
            }), cancellationToken: ct).ConfigureAwait(false);
        }
        await Transfers.UpdateOneAsync(new BsonDocument("_id", transferId), new BsonDocument("$set", new BsonDocument
        {
            ["st"] = StoragePoolConstants.Pending,
            ["npc"] = 0,
            ["ftt"] = BsonNull.Value,
            ["le"] = "",
        }), cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task RefreshHasDataAsync(ObjectId sourceId, ObjectId? targetId, CancellationToken ct)
    {
        var hasData = await Projects.Find(new BsonDocument("csid", sourceId)).AnyAsync(ct).ConfigureAwait(false);
        await SourceRows.UpdateOneAsync(new BsonDocument("_id", sourceId), new BsonDocument("$set", new BsonDocument
        {
            ["hd"] = hasData,
            ["ut"] = clock.GetUtcNow().UtcDateTime,
        }), cancellationToken: ct).ConfigureAwait(false);
        if (targetId is not null)
        {
            var targetHasData = await Projects.Find(new BsonDocument("csid", targetId.Value)).AnyAsync(ct)
                .ConfigureAwait(false);
            await SourceRows.UpdateOneAsync(new BsonDocument("_id", targetId.Value),
                new BsonDocument("$set", new BsonDocument
                {
                    ["hd"] = targetHasData,
                    ["ut"] = clock.GetUtcNow().UtcDateTime,
                }), cancellationToken: ct).ConfigureAwait(false);
        }
    }

    private Task<BsonDocument?> LoadTransferAsync(ObjectId transferId, CancellationToken ct) =>
        Transfers.Find(new BsonDocument("_id", transferId)).FirstOrDefaultAsync(ct)!;

    private Task<StorageBackendLease> GetLeaseAsync(bool pool, BsonDocument document, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!pool) return Task.FromResult(factory.BorrowDefault(defaultStorage));
        var descriptor = StoragePoolSourceService.Descriptor(document);
        return Task.FromResult(leaseFactory is null ? factory.CreateLease(descriptor) : leaseFactory(descriptor));
    }

    private StorageBackendDescriptor? DefaultDescriptor()
    {
        var type = (configuration["Storage:Type"] ?? configuration["Storage:BucketStyle"] ?? "S3").Trim();
        if (type.Equals("Local", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("FILESYSTEM", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("FILE", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("DISK", StringComparison.OrdinalIgnoreCase)) return null;
        var endpointText = configuration["Storage:Endpoint"];
        var bucket = configuration["Storage:Bucket"];
        var accessKey = configuration["Storage:AccessKey"];
        var secretKey = configuration["Storage:SecretKey"];
        if (string.IsNullOrWhiteSpace(endpointText) ||
            !Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
            string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            return null;
        return new StorageBackendDescriptor(
            endpoint,
            bucket,
            configuration["Storage:KeyPrefix"] ?? "",
            configuration["Storage:Region"] ?? "us-east-1",
            configuration.GetValue("Storage:ForcePathStyle", true),
            accessKey,
            secretKey,
            configuration["Storage:Domain"] ?? "",
            true);
    }

    private Task MarkObjectAsync(BsonDocument row, int status, string error, CancellationToken ct,
        bool incrementAttempt = false)
    {
        var set = new BsonDocument
        {
            ["st"] = status,
            ["le"] = error.Length > 500 ? error[..500] : error,
        };
        var update = incrementAttempt
            ? new BsonDocument { ["$set"] = set, ["$inc"] = new BsonDocument("at", 1) }
            : new BsonDocument("$set", set);
        return Objects.UpdateOneAsync(new BsonDocument("_id", row["_id"]), update, cancellationToken: ct);
    }

    private Task FailAsync(ObjectId transferId, string message, CancellationToken ct) =>
        Transfers.UpdateOneAsync(new BsonDocument("_id", transferId), new BsonDocument("$set", new BsonDocument
        {
            ["st"] = StoragePoolConstants.Failed,
            ["le"] = message,
            ["ftt"] = clock.GetUtcNow().UtcDateTime,
        }), cancellationToken: ct);

    private Task MarkAbortedAsync(ObjectId transferId, string message, CancellationToken ct) =>
        Transfers.UpdateOneAsync(new BsonDocument("_id", transferId), new BsonDocument("$set", new BsonDocument
        {
            ["st"] = StoragePoolConstants.Aborted,
            ["le"] = message,
            ["ftt"] = clock.GetUtcNow().UtcDateTime,
        }), cancellationToken: ct);

    private async Task StallOrFailAsync(ObjectId transferId, string message, CancellationToken ct,
        bool doneWithWarning = false)
    {
        var transfer = await LoadTransferAsync(transferId, ct).ConfigureAwait(false);
        if (transfer is null) return;
        var stalls = StoragePoolBson.Int(transfer, "npc") + 1;
        if (doneWithWarning && stalls >= StoragePoolConstants.MaxStalls)
        {
            await Transfers.UpdateOneAsync(new BsonDocument("_id", transferId), new BsonDocument("$set", new BsonDocument
            {
                ["st"] = StoragePoolConstants.Done,
                ["le"] = "源端残留对象未能全部删除，请人工检查",
                ["npc"] = stalls,
                ["ftt"] = clock.GetUtcNow().UtcDateTime,
            }), cancellationToken: ct).ConfigureAwait(false);
            return;
        }
        if (stalls >= StoragePoolConstants.MaxStalls)
        {
            await Transfers.UpdateOneAsync(new BsonDocument("_id", transferId), new BsonDocument("$set", new BsonDocument
            {
                ["st"] = StoragePoolConstants.Failed,
                ["le"] = message,
                ["npc"] = stalls,
                ["ftt"] = clock.GetUtcNow().UtcDateTime,
            }), cancellationToken: ct).ConfigureAwait(false);
            return;
        }
        await Transfers.UpdateOneAsync(new BsonDocument("_id", transferId), new BsonDocument("$set", new BsonDocument
        {
            ["npc"] = stalls,
            ["le"] = message,
        }), cancellationToken: ct).ConfigureAwait(false);
    }

    private static string Sanitize(Exception error)
    {
        var message = error.Message;
        if (message.Length > 300) message = message[..300];
        return message;
    }

    private sealed class CountingReadStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
