using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Modules.StoragePool;

public sealed class StoragePoolTransferService
{
    private readonly MongoStore store;
    private readonly StoragePoolTransferEngine engine;

    public StoragePoolTransferService(MongoStore store, StoragePoolTransferEngine engine)
    {
        this.store = store;
        this.engine = engine;
    }

    private IMongoCollection<BsonDocument> Transfers => store.Collection(StoragePoolConstants.TransferCollection);
    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private IMongoCollection<BsonDocument> Sources => store.Collection(StoragePoolConstants.SourceCollection);
    private IMongoCollection<BsonDocument> RunLocks => store.Collection(StoragePoolConstants.RunLockCollection);

    public async Task<(List<BsonDocument> Rows, long Count)> ListAsync(string? projectId, int skip, int limit,
        CancellationToken ct)
    {
        BsonDocument filter;
        if (string.IsNullOrWhiteSpace(projectId))
            filter = new BsonDocument();
        else if (!ObjectId.TryParse(projectId, out var parsed))
            return (new List<BsonDocument>(), 0);
        else
            filter = new BsonDocument("p", parsed);

        var rows = await Transfers.Find(filter)
            .Sort(new BsonDocument { ["ct"] = -1, ["_id"] = -1 })
            .Skip(skip).Limit(limit).ToListAsync(ct).ConfigureAwait(false);
        var count = await Transfers.CountDocumentsAsync(filter, cancellationToken: ct).ConfigureAwait(false);
        var views = new List<BsonDocument>(rows.Count);
        foreach (var row in rows)
        {
            await engine.RecomputeAsync(row["_id"].AsObjectId, ct).ConfigureAwait(false);
            var fresh = await Transfers.Find(new BsonDocument("_id", row["_id"])).FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (fresh is null) continue;
            var project = await LookupAsync(Projects, StoragePoolBson.ObjectId(fresh, "p"), ct).ConfigureAwait(false);
            var source = await LookupAsync(Sources, StoragePoolBson.ObjectId(fresh, "s"), ct).ConfigureAwait(false);
            var target = await LookupAsync(Sources, StoragePoolBson.ObjectId(fresh, "ts"), ct).ConfigureAwait(false);
            views.Add(StoragePoolTransferView.ToApi(fresh, project, source, target));
        }
        return (views, count);
    }

    public async Task<BsonDocument> GetAsync(string id, CancellationToken ct)
    {
        var key = ParseId(id);
        var row = await Transfers.Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new ApiError("StoragePoolTransferNotFound", 1, "迁移任务不存在", 404);
        var project = await LookupAsync(Projects, StoragePoolBson.ObjectId(row, "p"), ct).ConfigureAwait(false);
        var source = await LookupAsync(Sources, StoragePoolBson.ObjectId(row, "s"), ct).ConfigureAwait(false);
        var target = await LookupAsync(Sources, StoragePoolBson.ObjectId(row, "ts"), ct).ConfigureAwait(false);
        return StoragePoolTransferView.ToApi(row, project, source, target);
    }

    public Task CancelAsync(string id, CancellationToken ct) => engine.CancelAsync(ParseId(id), ct);

    public Task DeleteAsync(string id, CancellationToken ct) => engine.DeleteAsync(ParseId(id), ct);

    public async Task RetryAsync(string id, CancellationToken ct)
    {
        var key = ParseId(id);
        var transfer = await Transfers.Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new ApiError("StoragePoolTransferNotFound", 1, "迁移任务不存在", 404);
        if (!StoragePoolConstants.IsRetryable(StoragePoolBson.Int(transfer, "st")))
            throw new ApiError("StoragePoolTransferInProgress", 7, "任务不可重试", 409);
        var projectId = StoragePoolBson.ObjectId(transfer, "p");
        if (projectId is not null)
        {
            var project = await Projects.Find(new BsonDocument("_id", projectId.Value)).FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (project is not null && StoragePoolBson.Bool(project, "splk"))
                throw new ApiError("StoragePoolNotAllowed", 5, "项目存储位置已锁定", 409);
        }
        await engine.ResetForRetryAsync(key, ct).ConfigureAwait(false);
        Dispatch(key);
    }

    public void Dispatch(ObjectId transferId)
    {
        _ = Task.Run(async () =>
        {
            try { await engine.RunAsync(transferId, CancellationToken.None).ConfigureAwait(false); }
            catch { /* the transfer keeps a persisted checkpoint/error */ }
        });
    }

    public async Task RunSynchronouslyAsync(ObjectId transferId, CancellationToken ct) =>
        await engine.RunAsync(transferId, ct).ConfigureAwait(false);

    public async Task<bool> HasActiveAsync(ObjectId projectId, CancellationToken ct) =>
        await Transfers.Find(new BsonDocument
        {
            ["p"] = projectId,
            ["st"] = new BsonDocument("$in", new BsonArray
                { StoragePoolConstants.Pending, StoragePoolConstants.Copying, StoragePoolConstants.Verified, StoragePoolConstants.Switched }),
        }).AnyAsync(ct).ConfigureAwait(false);

    public async Task<bool> RunLockExistsAsync(ObjectId transferId, CancellationToken ct) =>
        await RunLocks.Find(new BsonDocument("_id", transferId)).AnyAsync(ct).ConfigureAwait(false);

    private static ObjectId ParseId(string id)
    {
        if (!ObjectId.TryParse(id, out var parsed))
            throw new ApiError("StoragePoolTransferNotFound", 1, "迁移任务不存在", 404);
        return parsed;
    }

    private static async Task<BsonDocument?> LookupAsync(IMongoCollection<BsonDocument> collection, ObjectId? id,
        CancellationToken ct)
    {
        if (id is null) return null;
        return await collection.Find(new BsonDocument("_id", id.Value)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }
}
