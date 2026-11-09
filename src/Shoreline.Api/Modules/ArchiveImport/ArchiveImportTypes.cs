using System.Globalization;
using MongoDB.Bson;

namespace Shoreline.Api.Modules.ArchiveImport;

/// <summary>Legacy archive_import_task status values (Python constants.py).</summary>
public static class ArchiveImportStatus
{
    public const int Queued = 0;
    public const int Resolving = 1;
    public const int Downloading = 2;
    public const int Validating = 3;
    public const int Importing = 4;
    public const int Succeeded = 5;
    public const int Failed = 6;

    public static bool IsRunning(int status) => status >= Queued && status <= Importing;

    public static readonly int[] Running = [Queued, Resolving, Downloading, Validating, Importing];
}

public static class ArchiveImportText
{
    public const string QueuedStage = "";
    public const string ResolvingStage = "解析归档链接";
    public const string DownloadingStage = "下载归档";
    public const string ValidatingStage = "校验归档";
    public const string ImportingStage = "导入图片";
    public const string FailedStage = "导入失败";

    public static string Name(int status) => status switch
    {
        ArchiveImportStatus.Queued => "排队中",
        ArchiveImportStatus.Resolving => "解析归档链接",
        ArchiveImportStatus.Downloading => "下载归档中",
        ArchiveImportStatus.Validating => "校验归档中",
        ArchiveImportStatus.Importing => "导入图片中",
        ArchiveImportStatus.Succeeded => "已完成",
        ArchiveImportStatus.Failed => "导入失败",
        _ => "未知状态",
    };

    public static string SucceededStage(int imported) => $"导入完成（{imported} 张）";
}

/// <summary>Per-process options; values intentionally mirror the Python module configuration.</summary>
public sealed class ArchiveImportOptions
{
    public const long DefaultMaxZipBytes = 500L * 1024 * 1024;
    public const long DefaultMaxUncompressedBytes = 2048L * 1024 * 1024;
    public const int DefaultMaxEntries = 5000;
    public const long DefaultMaxEntryBytes = 128L * 1024 * 1024;

    public string ProviderApiUrl { get; init; } = "";
    public IReadOnlyList<string> AllowedHosts { get; init; } = Array.Empty<string>();
    public long MaxZipBytes { get; init; } = DefaultMaxZipBytes;
    public long MaxUncompressedBytes { get; init; } = DefaultMaxUncompressedBytes;
    public int MaxEntries { get; init; } = DefaultMaxEntries;
    public long MaxEntryBytes { get; init; } = DefaultMaxEntryBytes;
    public string? TempRoot { get; init; }
    public TimeSpan ClaimLease { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan StaleTaskAge { get; init; } = TimeSpan.FromHours(1);

    public static ArchiveImportOptions From(IConfiguration configuration)
    {
        var archive = configuration.GetSection("Archive");
        return new ArchiveImportOptions
        {
            ProviderApiUrl = archive["ProviderApiUrl"] ?? configuration["ARCHIVE_PROVIDER_API_URL"] ?? "",
            AllowedHosts = archive.GetSection("AllowedHosts").Get<string[]>() ?? Array.Empty<string>(),
            MaxZipBytes = ReadLong(configuration, "Archive:MaxZipBytes", "ARCHIVE_MAX_ZIP_BYTES", DefaultMaxZipBytes),
            MaxUncompressedBytes = ReadUncompressedBytes(configuration),
            MaxEntries = (int)Math.Min(int.MaxValue,
                ReadLong(configuration, "Archive:MaxZipEntries", "ARCHIVE_MAX_ZIP_ENTRIES", DefaultMaxEntries)),
            MaxEntryBytes = ReadLong(configuration, "Archive:MaxEntryBytes", "ARCHIVE_MAX_ENTRY_BYTES", DefaultMaxEntryBytes),
        };
    }

    private static long ReadUncompressedBytes(IConfiguration configuration)
    {
        var direct = ReadOptionalLong(configuration, "Archive:MaxZipUncompressedBytes", "ARCHIVE_MAX_ZIP_UNCOMPRESSED_BYTES");
        if (direct is > 0) return direct.Value;
        var megabytes = ReadOptionalLong(configuration, "Archive:MaxZipUncompressedMB", "ARCHIVE_MAX_ZIP_UNCOMPRESSED_MB");
        if (megabytes is > 0 && megabytes.Value <= long.MaxValue / (1024 * 1024)) return megabytes.Value * 1024 * 1024;
        return DefaultMaxUncompressedBytes;
    }

    private static long ReadLong(IConfiguration configuration, string key, string environmentKey, long fallback)
    {
        var value = ReadOptionalLong(configuration, key, environmentKey);
        return value is > 0 ? value.Value : fallback;
    }

    private static long? ReadOptionalLong(IConfiguration configuration, string key, string environmentKey)
    {
        foreach (var raw in new[] { configuration[key], configuration[environmentKey] })
        {
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
                return value;
        }
        return null;
    }
}

/// <summary>Expected failure with a legacy-compatible Chinese stage/message; never contains credentials.</summary>
public sealed class ArchiveImportFailure(string stage, string message) : Exception(message)
{
    public string Stage { get; } = stage;
}

/// <summary>The per-project claim or the project label epoch changed under us.</summary>
public sealed class ArchiveLostException(string message) : Exception(message);

public sealed class ArchiveDownloadLimitException(long maxBytes)
    : Exception($"归档超过大小上限 {maxBytes} bytes");

public sealed class ArchiveUploaderUnavailableException()
    : Exception("文件服务不可用");

/// <summary>Small helpers for legacy BSON documents shared by the task store and the worker.</summary>
public static class ArchiveImportIds
{
    public static ObjectId? Id(BsonDocument document, string key) =>
        document.TryGetValue(key, out var value) ? IdValue(value) : null;

    public static ObjectId? IdValue(BsonValue value) => value switch
    {
        BsonObjectId objectId => objectId.Value,
        BsonString text when ObjectId.TryParse(text.Value, out var parsed) => parsed,
        _ => null,
    };

    public static string StringValue(BsonValue value) => value switch
    {
        BsonString text => text.Value,
        BsonObjectId objectId => objectId.Value.ToString(),
        BsonNull => "",
        _ => value.ToString() ?? "",
    };

    public static string TimeText(BsonDocument document, string key)
    {
        var value = Time(document, key);
        return value == default
            ? ""
            : value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture);
    }

    public static DateTime Time(BsonDocument document, string key)
    {
        if (!document.TryGetValue(key, out var value)) return default;
        return value switch
        {
            BsonDateTime dateTime => dateTime.ToUniversalTime(),
            BsonString text when DateTime.TryParse(text.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
            _ => default,
        };
    }

    public static BsonValue ObjectIdValue(ObjectId? id) =>
        id is null ? BsonNull.Value : new BsonObjectId(id.Value);
}
