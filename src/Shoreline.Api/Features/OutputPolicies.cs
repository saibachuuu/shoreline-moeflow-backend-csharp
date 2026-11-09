using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

public static class OutputTypes
{
    public const int All = 0;
    public const int OnlyText = 1;
}

public static class OutputStatus
{
    public const int Queuing = 0;
    public const int Downloading = 1;
    public const int TranslationOutputting = 2;
    public const int Ziping = 3;
    public const int Succeeded = 4;
    public const int Error = 5;
}

/// <summary>Python OutputStatus details, sorted by id (app/constants/output.py:16-23).</summary>
public static class OutputStatusDetails
{
    public static object[] All() =>
    [
        new { id = 0, name = "排队中", intro = "" },
        new { id = 1, name = "源文件整理中", intro = "" },
        new { id = 2, name = "翻译整理中", intro = "" },
        new { id = 3, name = "压缩中", intro = "" },
        new { id = 4, name = "已完成", intro = "" },
        new { id = 5, name = "导出错误，请重试", intro = "" }
    ];
}

public static class OutputPolicy
{
    public const int DefaultWaitSeconds = 300;

    public static int WaitSeconds(IConfiguration? configuration)
    {
        var text = configuration?["Outputs:WaitSeconds"];
        if (string.IsNullOrWhiteSpace(text)) text = configuration?["OUTPUT_WAIT_SECONDS"];
        return int.TryParse(text, out var seconds) && seconds >= 0 ? seconds : DefaultWaitSeconds;
    }

    /// <summary>
    /// True when the newest output is inside the Python cooldown window
    /// <c>utcnow - create_time &lt; OUTPUT_WAIT_SECONDS</c>.
    /// </summary>
    public static bool IsTooFast(BsonValue createTime, DateTime nowUtc, int waitSeconds)
    {
        if (createTime is not BsonDateTime date) return false;
        return nowUtc - date.ToUniversalTime() < TimeSpan.FromSeconds(waitSeconds);
    }
}

/// <summary>Python Project.files image filter and ordering used by exports.</summary>
public static class OutputFileSelection
{
    public const int FileTypeImage = 2;

    public static FilterDefinition<BsonDocument> Filter(
        ObjectId projectId,
        IReadOnlyCollection<ObjectId>? include,
        IReadOnlyCollection<ObjectId>? exclude)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("p", projectId)
                     & Builders<BsonDocument>.Filter.Eq("ac", true)
                     & Builders<BsonDocument>.Filter.Eq("t", FileTypeImage);
        if (include is { Count: > 0 })
            filter &= Builders<BsonDocument>.Filter.In("_id", include);
        if (exclude is { Count: > 0 })
            filter &= Builders<BsonDocument>.Filter.Nin("_id", exclude);
        return filter;
    }

    // Python's default project.files() order is exactly dn, t, sn. There is no
    // _id tie-break in the legacy query. Manual order is applied client-side by
    // OutputExportService because the required null-last mo order cannot be
    // expressed as a plain ascending Mongo sort.
    public static SortDefinition<BsonDocument> Sort(bool anyManual) =>
        Builders<BsonDocument>.Sort.Ascending("dn").Ascending("t").Ascending("sn");

    public static FilterDefinition<BsonDocument> ManualProbe(FilterDefinition<BsonDocument> filter) =>
        filter & Builders<BsonDocument>.Filter.Ne("mo", BsonNull.Value);
}
