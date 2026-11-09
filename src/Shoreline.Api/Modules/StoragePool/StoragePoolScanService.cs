using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Modules.StoragePool;

public sealed class StoragePoolScanService
{
    private readonly MongoStore store;
    private readonly StoragePoolSettingsService settings;
    private readonly StoragePoolTransferEngine engine;
    private readonly StoragePoolTransferService transfers;
    private readonly TimeProvider clock;

    public StoragePoolScanService(
        MongoStore store,
        StoragePoolSettingsService settings,
        StoragePoolTransferEngine engine,
        StoragePoolTransferService transfers,
        TimeProvider clock)
    {
        this.store = store;
        this.settings = settings;
        this.engine = engine;
        this.transfers = transfers;
        this.clock = clock;
    }

    private IMongoCollection<BsonDocument> Sources => store.Collection(StoragePoolConstants.SourceCollection);
    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private IMongoCollection<BsonDocument> Transfers => store.Collection(StoragePoolConstants.TransferCollection);
    private IMongoCollection<BsonDocument> Presence => store.Collection("project_presence");

    public async Task<BsonDocument> RunAsync(bool force, CancellationToken ct)
    {
        var setting = await settings.ReadAsync(ct).ConfigureAwait(false);
        var queued = 0;
        if (!force && !StoragePoolBson.Bool(setting, "se"))
        {
            return Result("自动扫描未启用", queued);
        }
        var sources = await Sources.Find(new BsonDocument("e", true)).ToListAsync(ct).ConfigureAwait(false);
        if (sources.Count == 0)
        {
            await settings.RecordScanAsync(clock.GetUtcNow().UtcDateTime, "没有启用的存储来源", 0, ct)
                .ConfigureAwait(false);
            return Result("没有启用的存储来源", 0);
        }

        var maxBytes = StoragePoolBson.Long(setting, "rmb", 20L * 1024 * 1024 * 1024);
        var maxProjects = Math.Clamp(StoragePoolBson.Int(setting, "rmp", 20), 1, 1000);
        var maxObjects = Math.Clamp(StoragePoolBson.Int(setting, "rmo", 2000), 1, 100000);
        var runEnabled = StoragePoolBson.Bool(setting, "re", true);
        var now = clock.GetUtcNow().UtcDateTime;
        long spent = 0;
        var started = 0;
        var oldestEdit = now.AddMinutes(-15);
        var heartbeat = now.AddMinutes(-5);

        foreach (var source in sources)
        {
            var sourceId = source["_id"].AsObjectId;
            var cutoff = now.AddDays(-Math.Clamp(StoragePoolBson.Int(source, "iad", 90), 1, 3650));
            var limit = Math.Min(200, maxObjects);
            var candidates = await Projects.Find(new BsonDocument
                {
                    ["st"] = 0,
                    ["cip"] = new BsonDocument("$ne", true),
                    ["csid"] = BsonNull.Value,
                    ["splk"] = new BsonDocument("$ne", true),
                    ["m_e"] = new BsonDocument("$lt", cutoff),
                }).Sort(new BsonDocument { ["m_e"] = 1, ["_id"] = 1 }).Limit(limit).ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var candidate in candidates)
            {
                if (started >= maxProjects) break;
                var projectId = candidate["_id"].AsObjectId;
                var fresh = await Projects.Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                if (fresh is null || StoragePoolBson.ObjectId(fresh, "csid") is not null ||
                    StoragePoolBson.Bool(fresh, "splk") ||
                    StoragePoolBson.Int(fresh, "st") != 0 ||
                    StoragePoolBson.Bool(fresh, "cip"))
                    continue;
                if ((StoragePoolBson.Date(fresh, "m_e") ?? DateTime.MinValue) >= cutoff) continue;
                if (await IsBusyAsync(projectId, oldestEdit, heartbeat, ct).ConfigureAwait(false)) continue;
                if (await HasActiveTransferAsync(projectId, ct).ConfigureAwait(false)) continue;
                var estimate = StoragePoolBson.Long(fresh, "fs") * 1024;
                if (started >= 1 && spent + estimate > maxBytes) break;
                var transfer = await engine.PrepareAsync(projectId, sourceId, StoragePoolConstants.DirectionIn,
                    null, ct).ConfigureAwait(false);
                started++;
                spent += estimate;
                if (!runEnabled) continue;
                if (transfer.TryGetValue("_id", out var id) && id.IsObjectId)
                {
                    transfers.Dispatch(id.AsObjectId);
                    queued++;
                }
            }
        }

        var message = runEnabled
            ? $"扫描完成，已排队 {queued} 个项目"
            : $"扫描完成，新增 {started} 个待执行项目（自动执行已关闭）";
        await settings.RecordScanAsync(now, message, queued, ct).ConfigureAwait(false);
        return Result(message, queued);
    }

    private async Task<bool> IsBusyAsync(ObjectId projectId, DateTime oldestEdit, DateTime heartbeat,
        CancellationToken ct)
    {
        var presence = await Presence.Find(new BsonDocument
        {
            ["project"] = projectId,
            ["$or"] = new BsonArray
            {
                new BsonDocument("expire_at", new BsonDocument("$gt", clock.GetUtcNow().UtcDateTime)),
                new BsonDocument("last_heartbeat", new BsonDocument("$gt", heartbeat)),
            },
        }).AnyAsync(ct).ConfigureAwait(false);
        if (presence) return true;
        var project = await Projects.Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return project is not null &&
               (StoragePoolBson.Date(project, "m_e") ?? DateTime.MinValue) > oldestEdit;
    }

    private Task<bool> HasActiveTransferAsync(ObjectId projectId, CancellationToken ct) =>
        Transfers.Find(new BsonDocument
        {
            ["p"] = projectId,
            ["st"] = new BsonDocument("$in", new BsonArray
                { StoragePoolConstants.Pending, StoragePoolConstants.Copying, StoragePoolConstants.Verified, StoragePoolConstants.Switched }),
        }).AnyAsync(ct);

    private static BsonDocument Result(string message, int queued) => new()
    {
        ["ok"] = true,
        ["message"] = message,
        ["queued"] = queued,
    };
}
