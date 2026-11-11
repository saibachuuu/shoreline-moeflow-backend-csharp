using MongoDB.Bson;
using Shoreline.Api.Formats;
using Microsoft.Extensions.Configuration;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Modules.StoragePool;

public static class StoragePoolConstants
{
    public const string SourceCollection = "storage_pool_source";
    public const string TransferCollection = "storage_transfer";
    public const string ObjectCollection = "storage_transfer_object";
    public const string SettingCollection = "storage_pool_setting";
    public const string LockCollection = "storage_pool_lock";
    public const string RunLockCollection = "storage_transfer_run_lock";
    public const string AuditCollection = "storage_pool_audit";

    public const int Pending = 0;
    public const int Copying = 1;
    public const int Verified = 2;
    public const int Switched = 3;
    public const int Done = 4;
    public const int Failed = 5;
    public const int Aborted = 6;
    public const int Cancelled = 7;

    public const int DirectionIn = 0;
    public const int DirectionOut = 1;
    public const int DirectionMove = 2;

    public const int ObjectPending = 0;
    public const int ObjectCopied = 1;
    public const int ObjectVerified = 2;
    public const int ObjectSourceDeleted = 3;
    public const int ObjectFailed = 4;
    public const int ObjectSkipped = 5;

    public const string KindOriginal = "original";
    public const string KindOutput = "output";
    public const string KindResample = "resample";
    public const string KindText = "text";

    public const string ProbeObjectName = ".moeflow-storage-probe";
    public const int MaxStalls = 3;
    public const int DeleteBatchSize = 1000;
    public const int LockTtlSeconds = 1800;

    public static bool IsActive(int status) => status is Pending or Copying or Verified or Switched;
    public static bool IsRetryable(int status) => status is Failed or Aborted;

    // Legacy TransferStatus.details / TransferDirection.details names. The admin
    // console renders status_name and direction_name verbatim (no i18n mapping),
    // so these stay the Chinese display strings the Python module returned, and
    // an unknown value falls back to "" exactly like get_detail_by_value does.
    public static string StatusName(int status) => status switch
    {
        Pending => "待执行",
        Copying => "搬运中",
        Verified => "已校验",
        Switched => "已切换",
        Done => "已完成",
        Failed => "失败",
        Aborted => "已中止",
        Cancelled => "已取消",
        _ => "",
    };

    public static string DirectionName(int direction) => direction switch
    {
        DirectionIn => "迁入存储池",
        DirectionOut => "回迁到默认存储",
        DirectionMove => "转到其他来源",
        _ => "",
    };

    internal static string KindForKey(string key, StoragePoolPrefixes prefixes)
    {
        if (key.StartsWith(prefixes.OutputPrefix, StringComparison.Ordinal)) return KindOutput;
        if (!key.StartsWith(prefixes.FilePrefix, StringComparison.Ordinal)) return KindOriginal;
        var fileName = key[prefixes.FilePrefix.Length..];
        if (fileName.StartsWith(prefixes.ResampleName + "-", StringComparison.Ordinal)) return KindResample;
        return KindOriginal;
    }
}

internal sealed record StoragePoolPrefixes(string FilePrefix, string OutputPrefix, string ResampleName)
{
    public static StoragePoolPrefixes From(IConfiguration configuration)
    {
        var file = StorageBackendDescriptor.NormalizePrefix(configuration["Storage:FilePrefix"] ?? "files/");
        var output = StorageBackendDescriptor.NormalizePrefix(configuration["Storage:OutputPrefix"] ?? "outputs/");
        var resample = (configuration["Storage:ResamplePrefix"] ?? "resample").Trim().Trim('/');
        return new(file, output, resample.Length == 0 ? "resample" : resample);
    }
}

internal static class StoragePoolBson
{
    public static string String(BsonDocument document, string key, string fallback = "") =>
        document.TryGetValue(key, out var value) && value.IsString ? value.AsString : fallback;

    public static bool Bool(BsonDocument document, string key, bool fallback = false) =>
        document.TryGetValue(key, out var value) && value.IsBoolean ? value.AsBoolean : fallback;

    public static long Long(BsonDocument document, string key, long fallback = 0) =>
        document.TryGetValue(key, out var value) && value.IsNumeric ? value.ToInt64() : fallback;

    public static int Int(BsonDocument document, string key, int fallback = 0) =>
        (int)Long(document, key, fallback);

    public static ObjectId? ObjectId(BsonDocument document, string key)
    {
        if (!document.TryGetValue(key, out var value) || value.IsBsonNull) return null;
        if (value.IsObjectId) return value.AsObjectId;
        return value.IsString && MongoDB.Bson.ObjectId.TryParse(value.AsString, out var parsed) ? parsed : null;
    }

    public static DateTime? Date(BsonDocument document, string key)
    {
        if (!document.TryGetValue(key, out var value) || !value.IsBsonDateTime) return null;
        return value.ToUniversalTime();
    }

    public static BsonValue DateOrNull(BsonDocument document, string key)
    {
        var value = Date(document, key);
        return value is null ? BsonNull.Value : (BsonValue)value.Value;
    }

    public static string Iso(BsonDocument document, string key) =>
        Date(document, key) is { } value ? PythonValue.Iso(value) : "";

    public static string Iso(DateTime? value) =>
        value is { } date ? PythonValue.Iso(date.ToUniversalTime()) : "";

    public static BsonValue Ref(ObjectId? id) => id is null ? BsonNull.Value : id.Value;

    public static string NormalizeSubPath(string? value)
    {
        var raw = (value ?? "").Trim().Trim('/');
        return raw.Length == 0 ? "" : raw + "/";
    }
}
