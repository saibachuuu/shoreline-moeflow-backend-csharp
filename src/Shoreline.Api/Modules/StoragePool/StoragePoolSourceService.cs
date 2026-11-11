using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Modules.StoragePool;

public sealed class StoragePoolSourceService
{
    private static readonly Regex SubPathPattern = new("^[A-Za-z0-9._\\-/]*$", RegexOptions.CultureInvariant);
    private readonly MongoStore store;
    private readonly StorageBackendFactory factory;
    private readonly TimeProvider clock;
    private readonly string probeFilePrefix;

    public StoragePoolSourceService(
        MongoStore store,
        StorageBackendFactory factory,
        TimeProvider clock,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        this.store = store;
        this.factory = factory;
        this.clock = clock;
        probeFilePrefix = configuration is null
            ? "files/"
            : StoragePoolPrefixes.From(configuration).FilePrefix;
    }

    private IMongoCollection<BsonDocument> Sources => store.Collection(StoragePoolConstants.SourceCollection);
    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private IMongoCollection<BsonDocument> Transfers => store.Collection(StoragePoolConstants.TransferCollection);

    public async Task<List<BsonDocument>> ListAsync(int skip, int limit, CancellationToken ct)
    {
        var rows = await Sources.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(new BsonDocument { ["ct"] = -1, ["_id"] = -1 })
            .Skip(skip).Limit(limit).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToApi).ToList();
    }

    public Task<long> CountAsync(CancellationToken ct) => Sources.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);

    public async Task<BsonDocument> GetAsync(string id, CancellationToken ct)
    {
        var row = await Sources.Find(new BsonDocument("_id", BsonFields.Id(id))).FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return row is null ? throw Missing() : ToApi(row);
    }

    public async Task<BsonDocument> CreateAsync(JsonElement data, ObjectId actor, CancellationToken ct)
    {
        _ = actor;
        var name = StoragePoolJson.RequiredString(data, "name", 64);
        var endpoint = StoragePoolJson.RequiredString(data, "endpoint", 255).TrimEnd('/');
        var bucket = StoragePoolJson.RequiredString(data, "bucket", 128);
        var subPath = NormalizeAndValidateSubPath(StoragePoolJson.OptionalString(data, "sub_path") ?? "storage/");
        var region = (StoragePoolJson.OptionalString(data, "region") ?? "auto").Trim();
        if (region.Length == 0) region = "auto";
        var addressing = StoragePoolJson.OptionalString(data, "addressing_style") ?? "path";
        if (addressing is not ("path" or "virtual")) throw ApiError.Field("addressing_style", "仅支持 path 或 virtual");
        var verifySsl = StoragePoolJson.OptionalBoolean(data, "verify_ssl") ?? true;
        if (!verifySsl) throw UnsupportedVerifySsl();
        var accessKey = StoragePoolJson.RequiredString(data, "access_key_id", 128);
        var secretKey = StoragePoolJson.RequiredString(data, "secret_access_key", 512);
        var publicDomain = (StoragePoolJson.OptionalString(data, "public_domain") ?? "").Trim();
        var remark = StoragePoolJson.OptionalString(data, "remark") ?? "";
        if (remark.EnumerateRunes().Count() > 255) throw ApiError.Field("remark", "内容过长");
        var inactiveDays = StoragePoolJson.OptionalInteger(data, "inactive_days") ?? 90;
        if (inactiveDays is < 1 or > 3650) throw ApiError.Field("inactive_days", "范围为 1 到 3650");
        var maxObjects = StoragePoolJson.OptionalInteger(data, "max_objects_per_run") ?? 2000;
        if (maxObjects is < 1 or > 100000) throw ApiError.Field("max_objects_per_run", "范围为 1 到 100000");
        var enabled = StoragePoolJson.OptionalBoolean(data, "enabled") ?? true;
        ValidateEndpointAndDomain(endpoint, publicDomain);

        var now = clock.GetUtcNow().UtcDateTime;
        var row = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = name,
            ["e"] = enabled,
            ["ep"] = endpoint,
            ["b"] = bucket,
            ["sp"] = subPath,
            ["r"] = region,
            ["as"] = addressing,
            ["vs"] = verifySsl,
            ["ak"] = accessKey,
            ["sk"] = secretKey,
            ["pd"] = publicDomain,
            ["rm"] = remark,
            ["iad"] = inactiveDays,
            ["mo"] = maxObjects,
            ["hd"] = false,
            ["ct"] = now,
            ["ut"] = now,
        };

        await ProbeAsync(row, ct).ConfigureAwait(false);
        await Sources.InsertOneAsync(row, cancellationToken: ct).ConfigureAwait(false);
        return ToApi(row);
    }

    public async Task<BsonDocument> UpdateAsync(string id, JsonElement data, ObjectId actor, CancellationToken ct)
    {
        _ = actor;
        var objectId = BsonFields.Id(id);
        var row = await Sources.Find(new BsonDocument("_id", objectId)).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw Missing();

        var old = row.DeepClone().AsBsonDocument;
        var updates = new BsonDocument();
        if (StoragePoolJson.Has(data, "name"))
            updates["n"] = StoragePoolJson.RequiredString(data, "name", 64);
        if (StoragePoolJson.Has(data, "enabled"))
            updates["e"] = StoragePoolJson.RequiredBoolean(data, "enabled");
        if (StoragePoolJson.Has(data, "endpoint"))
            updates["ep"] = StoragePoolJson.RequiredString(data, "endpoint", 255).TrimEnd('/');
        if (StoragePoolJson.Has(data, "bucket"))
            updates["b"] = StoragePoolJson.RequiredString(data, "bucket", 128);
        if (StoragePoolJson.Has(data, "sub_path"))
            updates["sp"] = NormalizeAndValidateSubPath(StoragePoolJson.OptionalString(data, "sub_path") ?? "");
        if (StoragePoolJson.Has(data, "region"))
        {
            var region = (StoragePoolJson.OptionalString(data, "region") ?? "").Trim();
            updates["r"] = region.Length == 0 ? "auto" : region;
        }
        if (StoragePoolJson.Has(data, "addressing_style"))
        {
            var addressing = StoragePoolJson.OptionalString(data, "addressing_style") ?? "";
            if (addressing is not ("path" or "virtual")) throw ApiError.Field("addressing_style", "仅支持 path 或 virtual");
            updates["as"] = addressing;
        }
        if (StoragePoolJson.Has(data, "verify_ssl"))
        {
            var verifySsl = StoragePoolJson.RequiredBoolean(data, "verify_ssl");
            if (!verifySsl) throw UnsupportedVerifySsl();
            updates["vs"] = true;
        }
        if (StoragePoolJson.Has(data, "access_key_id"))
        {
            var value = StoragePoolJson.OptionalString(data, "access_key_id")?.Trim() ?? "";
            if (value.Length > 0) updates["ak"] = value;
        }
        if (StoragePoolJson.Has(data, "secret_access_key"))
        {
            var value = StoragePoolJson.OptionalString(data, "secret_access_key") ?? "";
            if (value.Length > 0) updates["sk"] = value;
        }
        if (StoragePoolJson.Has(data, "public_domain"))
            updates["pd"] = (StoragePoolJson.OptionalString(data, "public_domain") ?? "").Trim();
        if (StoragePoolJson.Has(data, "remark"))
        {
            var remark = StoragePoolJson.OptionalString(data, "remark") ?? "";
            if (remark.EnumerateRunes().Count() > 255) throw ApiError.Field("remark", "内容过长");
            updates["rm"] = remark;
        }
        if (StoragePoolJson.Has(data, "inactive_days"))
        {
            var value = StoragePoolJson.RequiredInteger(data, "inactive_days");
            if (value is < 1 or > 3650) throw ApiError.Field("inactive_days", "范围为 1 到 3650");
            updates["iad"] = value;
        }
        if (StoragePoolJson.Has(data, "max_objects_per_run"))
        {
            var value = StoragePoolJson.RequiredInteger(data, "max_objects_per_run");
            if (value is < 1 or > 100000) throw ApiError.Field("max_objects_per_run", "范围为 1 到 100000");
            updates["mo"] = value;
        }

        var candidate = row.DeepClone().AsBsonDocument;
        candidate.Merge(updates, overwriteExistingElements: true);
        ValidateEndpointAndDomain(StoragePoolBson.String(candidate, "ep"), StoragePoolBson.String(candidate, "pd"));

        if (StoragePoolBson.Bool(old, "hd") && LocationChanged(old, candidate))
            throw LocationLocked();

        var probeNeeded =
            !Equals(StoragePoolBson.String(old, "ep"), StoragePoolBson.String(candidate, "ep")) ||
            !Equals(StoragePoolBson.String(old, "b"), StoragePoolBson.String(candidate, "b")) ||
            !Equals(StoragePoolBson.String(old, "sp"), StoragePoolBson.String(candidate, "sp")) ||
            !Equals(StoragePoolBson.String(old, "r"), StoragePoolBson.String(candidate, "r")) ||
            !Equals(StoragePoolBson.String(old, "as"), StoragePoolBson.String(candidate, "as")) ||
            !Equals(StoragePoolBson.String(old, "ak"), StoragePoolBson.String(candidate, "ak")) ||
            !Equals(StoragePoolBson.String(old, "sk"), StoragePoolBson.String(candidate, "sk")) ||
            !Equals(StoragePoolBson.Bool(old, "vs"), StoragePoolBson.Bool(candidate, "vs"));
        if (probeNeeded) await ProbeAsync(candidate, ct).ConfigureAwait(false);

        updates["ut"] = clock.GetUtcNow().UtcDateTime;
        var result = await Sources.UpdateOneAsync(new BsonDocument("_id", objectId),
            new BsonDocument("$set", updates), cancellationToken: ct).ConfigureAwait(false);
        if (result.MatchedCount == 0) throw Missing();
        var updated = await Sources.Find(new BsonDocument("_id", objectId)).FirstAsync(ct).ConfigureAwait(false);
        return ToApi(updated);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var objectId = BsonFields.Id(id);
        if (!await Sources.Find(new BsonDocument("_id", objectId)).AnyAsync(ct).ConfigureAwait(false)) throw Missing();
        if (await Projects.Find(new BsonDocument("csid", objectId)).AnyAsync(ct).ConfigureAwait(false))
            throw new ApiError("StoragePoolSourceInUse", 2, "存储来源仍有项目在用", 409);
        var activeFilter = new BsonDocument
        {
            ["st"] = new BsonDocument("$in", new BsonArray
                { StoragePoolConstants.Pending, StoragePoolConstants.Copying, StoragePoolConstants.Verified, StoragePoolConstants.Switched }),
            ["$or"] = new BsonArray
            {
                new BsonDocument("s", objectId),
                new BsonDocument("ts", objectId),
            },
        };
        if (await Transfers.Find(activeFilter).AnyAsync(ct).ConfigureAwait(false))
            throw new ApiError("StoragePoolSourceBusy", 6, "存储来源有进行中的迁移", 409);
        await Sources.DeleteOneAsync(new BsonDocument("_id", objectId), ct).ConfigureAwait(false);
    }

    public async Task<BsonDocument> TestAsync(string id, CancellationToken ct)
    {
        var row = await Sources.Find(new BsonDocument("_id", BsonFields.Id(id))).FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? throw Missing();
        try
        {
            await ProbeAsync(row, ct).ConfigureAwait(false);
        }
        catch (ApiError error)
        {
            // Legacy contract (storage_pool api.SourceTestAPI): a failed probe is
            // reported in-band as HTTP 200 {"ok": false, "message": ...} so the
            // admin console can show the probe message verbatim. Only the probe
            // result is swallowed here; a missing source still 404s.
            return new BsonDocument { ["ok"] = false, ["message"] = error.Message };
        }
        return new BsonDocument { ["ok"] = true, ["message"] = "存储来源连接正常" };
    }

    public async Task ProbeAsync(BsonDocument source, CancellationToken ct)
    {
        StorageBackendLease? lease = null;
        var key = probeFilePrefix + StoragePoolConstants.ProbeObjectName + "-" + Guid.NewGuid().ToString("N");
        try
        {
            lease = factory.CreateLease(Descriptor(source));
            var bytes = Encoding.UTF8.GetBytes("shoreline-storage-probe");
            using (var input = new MemoryStream(bytes, writable: false))
                await lease.Backend.PutAsync(key, input, "text/plain", ct).ConfigureAwait(false);
            var head = await lease.Backend.HeadAsync(key, ct).ConfigureAwait(false);
            if (head is null || head.Size != bytes.Length)
                throw new InvalidOperationException("probe head mismatch");
            using (var download = await lease.Backend.GetAsync(key, ct).ConfigureAwait(false))
            {
                using var copy = new MemoryStream();
                await download.Content.CopyToAsync(copy, ct).ConfigureAwait(false);
                if (copy.Length != bytes.Length) throw new InvalidOperationException("probe get mismatch");
            }
        }
        catch (ApiError) { throw; }
        catch (Exception)
        {
            throw new ApiError("StoragePoolUnreachable", 4, "存储来源连接测试失败", 400);
        }
        finally
        {
            if (lease is not null)
            {
                try { await lease.Backend.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false); }
                catch { /* best-effort probe cleanup */ }
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public static StorageBackendDescriptor Descriptor(BsonDocument source) => new(
        new Uri(StoragePoolBson.String(source, "ep"), UriKind.Absolute),
        StoragePoolBson.String(source, "b"),
        StoragePoolBson.String(source, "sp"),
        StoragePoolBson.String(source, "r", "auto"),
        !string.Equals(StoragePoolBson.String(source, "as", "path"), "virtual", StringComparison.OrdinalIgnoreCase),
        StoragePoolBson.String(source, "ak"),
        StoragePoolBson.String(source, "sk"),
        StoragePoolBson.String(source, "pd"),
        StoragePoolBson.Bool(source, "vs", true));

    public static BsonDocument ToApi(BsonDocument source)
    {
        var accessKey = StoragePoolBson.String(source, "ak");
        var masked = accessKey.Length <= 8 ? new string('*', accessKey.Length) : accessKey[..4] + "..." + accessKey[^4..];
        return new BsonDocument
        {
            ["id"] = StoragePoolBson.ObjectId(source, "_id")?.ToString() ?? "",
            ["name"] = StoragePoolBson.String(source, "n"),
            ["enabled"] = StoragePoolBson.Bool(source, "e", true),
            ["endpoint"] = StoragePoolBson.String(source, "ep"),
            ["bucket"] = StoragePoolBson.String(source, "b"),
            ["sub_path"] = StoragePoolBson.NormalizeSubPath(StoragePoolBson.String(source, "sp")),
            ["region"] = StoragePoolBson.String(source, "r", "auto"),
            ["addressing_style"] = StoragePoolBson.String(source, "as", "path"),
            ["verify_ssl"] = StoragePoolBson.Bool(source, "vs", true),
            ["access_key_id"] = masked,
            ["public_domain"] = StoragePoolBson.String(source, "pd"),
            ["remark"] = StoragePoolBson.String(source, "rm"),
            ["inactive_days"] = StoragePoolBson.Int(source, "iad", 90),
            ["max_objects_per_run"] = StoragePoolBson.Int(source, "mo", 2000),
            ["has_data"] = StoragePoolBson.Bool(source, "hd"),
            ["create_time"] = StoragePoolBson.Iso(source, "ct"),
            ["update_time"] = StoragePoolBson.Iso(source, "ut"),
        };
    }

    public static ApiError Missing() => new("StoragePoolSourceNotFound", 1, "存储来源不存在", 404);
    public static ApiError UnsupportedVerifySsl() =>
        new("StoragePoolUnreachable", 4, "verify_ssl=false 不受支持，已拒绝连接", 400);
    public static ApiError LocationLocked() =>
        new("StoragePoolLocationLocked", 3, "存储来源已有数据，不能修改位置", 409);

    private static bool LocationChanged(BsonDocument oldRow, BsonDocument newRow) =>
        !Equals(StoragePoolBson.String(oldRow, "ep"), StoragePoolBson.String(newRow, "ep")) ||
        !Equals(StoragePoolBson.String(oldRow, "b"), StoragePoolBson.String(newRow, "b")) ||
        !Equals(StoragePoolBson.NormalizeSubPath(StoragePoolBson.String(oldRow, "sp")),
            StoragePoolBson.NormalizeSubPath(StoragePoolBson.String(newRow, "sp"))) ||
        !Equals(StoragePoolBson.String(oldRow, "r"), StoragePoolBson.String(newRow, "r")) ||
        !Equals(StoragePoolBson.String(oldRow, "as"), StoragePoolBson.String(newRow, "as"));

    private static string NormalizeAndValidateSubPath(string value)
    {
        var raw = (value ?? "").Trim();
        if (!SubPathPattern.IsMatch(raw))
            throw ApiError.Field("sub_path", "仅允许字母、数字、点、下划线、短横线和斜杠");
        return StoragePoolBson.NormalizeSubPath(raw);
    }

    private static void ValidateEndpointAndDomain(string endpoint, string publicDomain)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw ApiError.Field("endpoint", "必须是无用户信息、查询参数和片段的 http(s) 地址");
        if (publicDomain.Length > 0)
        {
            if (!Uri.TryCreate(publicDomain, UriKind.Absolute, out var domain) ||
                (domain.Scheme != Uri.UriSchemeHttp && domain.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(domain.Host) || !string.IsNullOrEmpty(domain.UserInfo) ||
                !string.IsNullOrEmpty(domain.Query) || !string.IsNullOrEmpty(domain.Fragment))
                throw ApiError.Field("public_domain", "必须是无用户信息、查询参数和片段的 http(s) 地址");
        }
    }
}

internal static class StoragePoolJson
{
    public static bool Has(JsonElement data, string field) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(field, out _);

    public static string RequiredString(JsonElement data, string field, int maxLength)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(field, out var value) ||
            value.ValueKind != JsonValueKind.String)
            throw ApiError.Field(field, "缺少必填字段");
        var text = value.GetString()!.Trim();
        if (text.Length == 0) throw ApiError.Field(field, "不能为空");
        if (text.EnumerateRunes().Count() > maxLength) throw ApiError.Field(field, "内容过长");
        return text;
    }

    public static string? OptionalString(JsonElement data, string field)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(field, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw ApiError.Field(field, "必须是字符串");
        return value.GetString();
    }

    public static bool RequiredBoolean(JsonElement data, string field)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(field, out var value))
            throw ApiError.Field(field, "缺少必填字段");
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        throw ApiError.Field(field, "布尔值格式不正确");
    }

    public static bool? OptionalBoolean(JsonElement data, string field)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(field, out _)) return null;
        return RequiredBoolean(data, field);
    }

    public static long RequiredInteger(JsonElement data, string field)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(field, out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            throw ApiError.Field(field, "整数格式不正确");
        return number;
    }

    public static int? OptionalInteger(JsonElement data, string field)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(field, out _)) return null;
        var value = RequiredInteger(data, field);
        if (value is < int.MinValue or > int.MaxValue) throw ApiError.Field(field, "整数超出范围");
        return (int)value;
    }
}
