using System.Text;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

public sealed partial class FileService
{
    private static readonly Dictionary<int, string> ParseStatusNames = new()
    {
        [0] = "解析未开始",
        [1] = "解析排队中",
        [2] = "解析中",
        [3] = "解析失败",
        [4] = "解析成功",
    };

    private static readonly Dictionary<int, string> ImageParseStatusNames = new()
    {
        [0] = "自动标记未开始",
        [1] = "排队中",
        [2] = "自动标记中",
        [3] = "自动标记失败",
        [4] = "自动标记完成",
    };

    private static readonly Dictionary<int, string> ParseErrorTypeNames = new()
    {
        [0] = "其他错误",
        [1] = "未知字符集",
        [2] = "文件无法读取，请确认文件完好或尝试重新上传",
        [3] = "图片读取失败，请稍后再试（1）",
        [4] = "图片读取失败，请稍后再试（2）",
        [5] = "图片超过 20MB 无法标记",
        [6] = "自动标记服务离线，请稍后再试",
    };

    private static readonly Dictionary<int, string> ImageOcrPercentNames = new()
    {
        [0] = "已加入队列",
        [1] = "重试中",
        [10] = "翻找图片中",
        [20] = "整理数据中",
        [55] = "图片识别中",
        [90] = "标记中",
        [100] = "自动标记完成",
    };

    // English msgstr values come from app/translations/en/LC_MESSAGES/messages.po;
    // Python resolves them with flask_babel lazy_gettext at request time.
    private static readonly Dictionary<int, string> ParseStatusNamesEn = new()
    {
        [0] = "Analysis not started.",
        [1] = "Parsing in queue.",
        [2] = "Analyzing.",
        [3] = "Parsing failed.",
        [4] = "Parsing successful.",
    };

    private static readonly Dictionary<int, string> ImageParseStatusNamesEn = new()
    {
        [0] = "Auto Mark as Not Started",
        [1] = "In queue.",
        [2] = "Auto-tagging in progress.",
        [3] = "Automatic tagging failed.",
        [4] = "Auto Mark Complete",
    };

    private static readonly Dictionary<int, string> ParseErrorTypeNamesEn = new()
    {
        [0] = "Other error.",
        [1] = "Unknown character set.",
        [2] = "The file cannot be read. Please ensure the file is intact or try re-uploading.",
        [3] = "Failed to load image, please try again later (1).",
        [4] = "Failed to load image, please try again later (2).",
        [5] = "Images larger than 20MB cannot be tagged.",
        [6] = "Automatic tagging service is offline, please try again later.",
    };

    private static readonly Dictionary<int, string> ImageOcrPercentNamesEn = new()
    {
        [0] = "Queued",
        [1] = "Retrying.",
        [10] = "Searching in images.",
        [20] = "Organizing data.",
        [55] = "Image recognition in progress.",
        [90] = "Marked in progress.",
        [100] = "Auto Mark Complete",
    };

    private bool ChinesePreferred()
    {
        try { return LanguageCatalog.ChinesePreferred(_http?.HttpContext?.Request.GetTypedHeaders().AcceptLanguage); }
        catch (FormatException) { return false; }
    }

    private static string DetailName(IReadOnlyDictionary<int, string> names, IReadOnlyDictionary<int, string> english, long value, bool chinese)
    {
        var source = chinese ? names : english;
        return value is >= int.MinValue and <= int.MaxValue && source.TryGetValue((int)value, out var name) ? name : "";
    }

    private async Task<Dictionary<string, object?>> ToApiAsync(
        BsonDocument file,
        BsonDocument? project,
        CancellationToken ct)
    {
        var type = file.Integer("t");
        var parent = file.Contains("f") && !file["f"].IsBsonNull ? file["f"].AsObjectId.ToString() : null;
        var parseStatus = file.Integer("ps");
        // app/models/file.py serializes an absent parse_error_type as null.
        int? parseError = file.Contains("pe") && !file["pe"].IsBsonNull ? file["pe"].ToInt32() : null;

        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = file["_id"].AsObjectId.ToString(),
            ["name"] = file.Text("n"),
            ["manual_order"] = file.Contains("mo") && !file["mo"].IsBsonNull ? file["mo"].ToInt32() : null,
            ["save_name"] = file.Text("sa"),
            ["type"] = type,
            ["file_size"] = file.Contains("fs") && !file["fs"].IsBsonNull ? file["fs"].ToInt64() : 0L,
            ["cover_bytes"] = file.Contains("cover_bytes") && !file["cover_bytes"].IsBsonNull ? file["cover_bytes"].ToInt64() : 0L,
            ["resample_bytes"] = file.Contains("resample_bytes") && !file["resample_bytes"].IsBsonNull ? file["resample_bytes"].ToInt64() : 0L,
            ["source_count"] = file.Integer("sc"),
            ["translated_source_count"] = file.Integer("tsc"),
            ["checked_source_count"] = file.Integer("csc"),
            ["file_not_exist_reason"] = file.Integer("fn"),
            ["safe_status"] = file.Integer("ss"),
            ["parse_status"] = parseStatus,
            ["parse_error_type"] = parseError,
            ["parse_error_type_detail_name"] = parseError is int errorValue ? DetailName(ParseErrorTypeNames, ParseErrorTypeNamesEn, errorValue, ChinesePreferred()) : "",
            ["parent_id"] = parent,
        };

        if (type == TypeImage)
        {
            var saveName = file.Text("sa");
            var thumbnailStatus = file.Integer("th");
            await using var storageLease = await LeaseForProjectAsync(project, ct);
            var storage = storageLease?.Backend ?? _storage;
            data["parse_status_detail_name"] = DetailName(ImageParseStatusNames, ImageParseStatusNamesEn, parseStatus, ChinesePreferred());
            data["url"] = await OriginalUrlAsync(saveName, storage, ct);
            data["cover_url"] = await ProcessedUrlAsync(file, storage, "cover", thumbnailStatus, ct);
            data["resample_url"] = await ProcessedUrlAsync(file, storage, "resample", thumbnailStatus, ct);
            data["thumbnail_status"] = thumbnailStatus;
            data["thumbnail_error"] = file.Text("the");
            var ocrPercent = file.Integer("op");
            data["image_ocr_percent"] = ocrPercent;
            data["image_ocr_percent_detail_name"] = DetailName(ImageOcrPercentNames, ImageOcrPercentNamesEn, ocrPercent, ChinesePreferred());
        }
        else
        {
            data["parse_status_detail_name"] = DetailName(ParseStatusNames, ParseStatusNamesEn, parseStatus, ChinesePreferred());
        }

        return data;
    }

    private static bool HasStoragePool(BsonDocument? project)
        => project is not null
           && project.Contains("csid")
           && !project["csid"].IsBsonNull
           && project["csid"].ToJson().Trim('"').Length > 0;

    private async Task<string> OriginalUrlAsync(string saveName, IStorageBackend storage,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(saveName))
        {
            return "";
        }

        return await storage.SignGetAsync(FilePrefix + saveName, UrlValidity);
    }

    private async Task<string> ProcessedUrlAsync(
        BsonDocument file,
        IStorageBackend storage,
        string processName,
        long thumbnailStatus,
        CancellationToken ct)
    {
        var saveName = file.Text("sa");
        if (string.IsNullOrEmpty(saveName))
        {
            return "";
        }

        var stem = saveName;
        var dot = saveName.LastIndexOf('.');
        if (dot >= 0)
        {
            stem = saveName[..dot];
        }

        var key = FilePrefix + processName + "-" + stem + ".webp";
        // app/models/file.py:758-800: object storage signs without probing when
        // the row is SUCCEEDED or when the legacy status is UNKNOWN; only local
        // storage (or an explicit failure state) probes the backend.
        var objectStorage = storage is not LocalStorageBackend;
        if (objectStorage && thumbnailStatus is 0 or 3)
            return await storage.SignGetAsync(key, UrlValidity);
        var exists = await storage.HeadAsync(key, ct);
        return exists is null ? "generating" : await storage.SignGetAsync(key, UrlValidity);
    }

    private Dictionary<string, object?> CacheApi(BsonDocument cache) => new(StringComparer.Ordinal)
    {
        ["id"] = cache["_id"].AsObjectId.ToString(),
        ["translated_source_count"] = cache.Integer("ts"),
        ["checked_source_count"] = cache.Integer("cs"),
    };

    private static Dictionary<string, object?> EmptyCacheApi() => new(StringComparer.Ordinal)
    {
        ["id"] = null,
        ["translated_source_count"] = 0,
        ["checked_source_count"] = 0,
    };

    private static string PythonJsonString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Python's json.dumps(..., ensure_ascii=False) default separators are ", " and
    /// ": ".  Manual_order is either an integer or null.
    /// </summary>
    public static string SnapshotJson(IReadOnlyList<BsonDocument> files)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < files.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            var file = files[i];
            var manual = file.Contains("mo") && !file["mo"].IsBsonNull
                ? file["mo"].ToInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "null";
            sb.Append('[')
                .Append(PythonJsonString(file["_id"].AsObjectId.ToString()))
                .Append(", ")
                .Append(PythonJsonString(file.Text("n")))
                .Append(", ")
                .Append(manual)
                .Append(']');
        }

        sb.Append(']');
        return sb.ToString();
    }

    public static string SnapshotVersion(IReadOnlyList<BsonDocument> files)
    {
        var bytes = Encoding.UTF8.GetBytes(SnapshotJson(files));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static readonly Utf8StringComparer Utf8Comparer = new();

    private sealed class Utf8StringComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            var left = Encoding.UTF8.GetBytes(x ?? "");
            var right = Encoding.UTF8.GetBytes(y ?? "");
            return left.AsSpan().SequenceCompareTo(right);
        }
    }
}
