using System.Globalization;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Modules.StoragePool;

public sealed class StoragePoolSettingsService
{
    private readonly MongoStore store;
    private readonly TimeProvider clock;

    public StoragePoolSettingsService(MongoStore store, TimeProvider clock)
    {
        this.store = store;
        this.clock = clock;
    }

    private IMongoCollection<BsonDocument> Settings => store.Collection(StoragePoolConstants.SettingCollection);

    public async Task<BsonDocument> ReadAsync(CancellationToken ct)
    {
        var existing = await Settings.Find(new BsonDocument("_id", "default")).FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (existing is not null) return existing;
        var defaults = Defaults(clock.GetUtcNow().UtcDateTime);
        try
        {
            await Settings.InsertOneAsync(defaults, cancellationToken: ct).ConfigureAwait(false);
            return defaults;
        }
        catch (MongoWriteException error) when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return await Settings.Find(new BsonDocument("_id", "default")).FirstAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<BsonDocument> GetViewAsync(CancellationToken ct)
    {
        var row = await ReadAsync(ct).ConfigureAwait(false);
        var view = ToApi(row);
        view["next_run_at"] = NextRunAt().ToString("yyyy-MM-ddTHH:mm:ss.ffffff",
            System.Globalization.CultureInfo.InvariantCulture);
        return view;
    }

    public async Task<BsonDocument> UpdateAsync(JsonElement data, ObjectId actor, CancellationToken ct)
    {
        var current = await ReadAsync(ct).ConfigureAwait(false);
        var updates = new BsonDocument();
        if (StoragePoolJson.Has(data, "scan_enabled"))
            AddChanged(updates, current, "se", StoragePoolJson.RequiredBoolean(data, "scan_enabled"));
        if (StoragePoolJson.Has(data, "run_enabled"))
            AddChanged(updates, current, "re", StoragePoolJson.RequiredBoolean(data, "run_enabled"));
        if (StoragePoolJson.Has(data, "run_max_bytes"))
        {
            var value = StoragePoolJson.RequiredInteger(data, "run_max_bytes");
            if (value < 1024 * 1024) throw ApiError.Field("run_max_bytes", "不得小于 1 MiB");
            AddChanged(updates, current, "rmb", value);
        }
        if (StoragePoolJson.Has(data, "run_max_objects"))
        {
            var value = StoragePoolJson.RequiredInteger(data, "run_max_objects");
            if (value is < 1 or > 100000) throw ApiError.Field("run_max_objects", "范围为 1 到 100000");
            AddChanged(updates, current, "rmo", value);
        }
        if (StoragePoolJson.Has(data, "run_max_projects"))
        {
            var value = StoragePoolJson.RequiredInteger(data, "run_max_projects");
            if (value is < 1 or > 1000) throw ApiError.Field("run_max_projects", "范围为 1 到 1000");
            AddChanged(updates, current, "rmp", value);
        }
        if (updates.ElementCount > 0)
        {
            updates["ub"] = actor;
            updates["ut"] = clock.GetUtcNow().UtcDateTime;
            await Settings.UpdateOneAsync(new BsonDocument("_id", "default"),
                new BsonDocument("$set", updates), cancellationToken: ct).ConfigureAwait(false);
            await store.Collection(StoragePoolConstants.AuditCollection).InsertOneAsync(new BsonDocument
            {
                ["a"] = actor,
                ["c"] = "settings.update",
                ["v"] = updates.DeepClone(),
                ["ct"] = clock.GetUtcNow().UtcDateTime,
            }, cancellationToken: ct).ConfigureAwait(false);
        }
        return await GetViewAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Claims the one scheduled scan for a UTC day. The claim is stored in
    /// Mongo so a restarted process (or a second replica) cannot repeat the
    /// daily scan. A false result means another worker already consumed the
    /// day, or this process already did.
    /// </summary>
    internal async Task<bool> TryClaimScheduledScanAsync(DateOnly day, CancellationToken ct)
    {
        var value = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var result = await Settings.UpdateOneAsync(new BsonDocument
            {
                ["_id"] = "default",
                ["lsd"] = new BsonDocument("$ne", value),
            },
            new BsonDocument("$set", new BsonDocument("lsd", value)),
            cancellationToken: ct).ConfigureAwait(false);
        return result.ModifiedCount == 1;
    }

    public Task RecordScanAsync(DateTime? scannedAt, string result, int queued, CancellationToken ct)
    {
        var set = new BsonDocument
        {
            ["lst"] = scannedAt is null ? BsonNull.Value : scannedAt.Value,
            ["lsr"] = result.Length > 500 ? result[..500] : result,
            ["lsq"] = queued,
        };
        return Settings.UpdateOneAsync(new BsonDocument("_id", "default"),
            new BsonDocument("$set", set), cancellationToken: ct);
    }

    public static BsonDocument Defaults(DateTime now) => new()
    {
        ["_id"] = "default",
        ["se"] = false,
        ["re"] = true,
        ["rmb"] = 20L * 1024 * 1024 * 1024,
        ["rmo"] = 2000,
        ["rmp"] = 20,
        ["ub"] = BsonNull.Value,
        ["ut"] = now,
        ["lst"] = BsonNull.Value,
        ["lsr"] = "",
        ["lsq"] = 0,
        ["lsd"] = BsonNull.Value,
    };

    public static BsonDocument ToApi(BsonDocument row) => new()
    {
        ["scan_enabled"] = StoragePoolBson.Bool(row, "se"),
        ["run_enabled"] = StoragePoolBson.Bool(row, "re", true),
        ["run_max_bytes"] = StoragePoolBson.Long(row, "rmb", 20L * 1024 * 1024 * 1024),
        ["run_max_objects"] = StoragePoolBson.Int(row, "rmo", 2000),
        ["run_max_projects"] = StoragePoolBson.Int(row, "rmp", 20),
        ["updated_by"] = StoragePoolBson.ObjectId(row, "ub") is { } updatedBy
            ? (BsonValue)updatedBy.ToString()
            : BsonNull.Value,
        ["update_time"] = StoragePoolBson.Iso(row, "ut"),
        ["last_scan_time"] = StoragePoolBson.Iso(row, "lst"),
        ["last_scan_result"] = StoragePoolBson.String(row, "lsr"),
        ["last_scan_queued"] = StoragePoolBson.Int(row, "lsq"),
    };

    private DateTime NextRunAt()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, 3, 0, 0, TimeSpan.Zero);
        if (next <= now) next = next.AddDays(1);
        return next.UtcDateTime;
    }

    private static void AddChanged(BsonDocument updates, BsonDocument current, string key, BsonValue value)
    {
        if (current.TryGetValue(key, out var oldValue) && oldValue.Equals(value)) return;
        updates[key] = value;
    }
}
