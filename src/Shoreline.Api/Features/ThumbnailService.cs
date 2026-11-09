using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Storage;
using Shoreline.Imaging;

namespace Shoreline.Api.Features;

/// <summary>
/// Extension seam for D1 uploads. D1 may depend on this interface without
/// taking a dependency on the thumbnail implementation.
/// </summary>
public interface IThumbnailScheduler
{
    Task<bool> ScheduleAsync(
        ObjectId fileId,
        ObjectId projectId,
        long? expectedLabelRevision,
        CancellationToken ct);
}

/// <summary>
/// Queues and runs the legacy-compatible thumbnail task.
///
/// The job document is the durable source of truth. The file state is set to
/// QUEUING only after the durable job exists, and the handler re-reads both the
/// project revision and the file save_name under the project label lock before
/// publishing anything. A stale task is therefore a no-op instead of
/// overwriting a newer upload or restore.
/// </summary>
public sealed partial class ThumbnailService : IThumbnailScheduler
{
    public const string JobKind = "create_thumbnail";
    public const string SourceMissingError = "source file is missing";
    public const string TransientFailureError = "thumbnail generation failed";
    public const string StoragePoolError = "storage pool routing is not available";
    public const int MaxErrorLength = 500;

    public const int ThumbnailUnknown = 0;
    public const int ThumbnailQueuing = 1;
    public const int ThumbnailGenerating = 2;
    public const int ThumbnailSucceeded = 3;
    public const int ThumbnailFailed = 4;

    private const int FileTypeImage = 2;

    private readonly MongoStore _store;
    private readonly ObjectStorage _storage;
    private readonly JobQueue _queue;
    private readonly ProjectLabelLock _labels;
    private readonly LabelRequestContext _labelContext;
    private readonly TimeProvider _clock;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ThumbnailService>? _logger;
    private readonly ImageProcessor _processor;

    public ThumbnailService(
        MongoStore store,
        ObjectStorage storage,
        JobQueue queue,
        ProjectLabelLock labels,
        LabelRequestContext labelContext,
        TimeProvider clock,
        IConfiguration configuration,
        ImageProcessor processor,
        ILogger<ThumbnailService>? logger = null)
    {
        _store = store;
        _storage = storage;
        _queue = queue;
        _labels = labels;
        _labelContext = labelContext;
        _clock = clock;
        _configuration = configuration;
        _processor = processor;
        _logger = logger;

        _maxAttempts = Math.Max(1, configuration.GetValue("Thumbnails:MaxAttempts", 4));
        _runLease = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Thumbnails:RunLeaseMinutes", 5)));
        _maxSourceBytes = Math.Max(1, configuration.GetValue("Thumbnails:MaxSourceBytes", 64L * 1024 * 1024));
        _tempRoot = configuration["Thumbnails:TempRoot"]
            ?? Path.Combine(Path.GetTempPath(), "shoreline-thumbnails");
    }

    private readonly int _maxAttempts;
    private readonly TimeSpan _runLease;
    private readonly long _maxSourceBytes;
    private readonly string _tempRoot;

    private IMongoCollection<BsonDocument> Files => _store.Collection("file");
    private IMongoCollection<BsonDocument> Projects => _store.Collection("project");

    /// <summary>
    /// Local/S3/R2 all use the local generation path. "server-side" is the
    /// legacy Aliyun mode; the C# storage layer refuses Aliyun endpoints, so
    /// this mode only returns the legacy "nothing to rebuild" response.
    /// </summary>
    public bool LocalGenerationRequired
    {
        get
        {
            var mode = _configuration["Thumbnails:Mode"] ?? "local";
            return !(string.Equals(mode, "server", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "server-side", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "oss", StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task<BsonDocument> GetFileAsync(string fileId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(fileId, out var id)) throw FileNotExist();
        var file = await Files.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        return file ?? throw FileNotExist();
    }

    public static ObjectId ProjectIdOf(BsonDocument file)
    {
        if (!file.TryGetValue("p", out var project) || !project.IsObjectId)
        {
            throw FileNotExist();
        }
        return project.AsObjectId;
    }

    public static ApiError FileNotExist() =>
        new("FileNotExistError", 8001, "文件/文件夹不存在，可能已被删除");

    public static ApiError FileTypeNotSupport() =>
        new("FileTypeNotSupportError", 108, "文件类型不支持此操作: 仅图片文件可生成缩略图");

    public static ApiError StoragePoolUnsupported() =>
        new("StoragePoolUnsupported", 2,
            "当前切片尚未接线存储池路由，无法在迁移后的存储池项目上执行文件操作", 409);

    public static ApiError NoProjectAccess() =>
        new("NoPermissionError", 103, "您没有此项目的访问权限");

    /// <summary>
    /// Enqueues one image. The file and project rows are re-read under the
    /// borrowed project label lock, so a request that came through the label
    /// guard cannot race the save_name/revision it is scheduling.
    /// </summary>
    public async Task<bool> ScheduleAsync(
        ObjectId fileId,
        ObjectId projectId,
        long? expectedLabelRevision,
        CancellationToken ct)
    {
        if (!LocalGenerationRequired) return false;

        await using var session = await _labels.EnterAsync(
            _labelContext, projectId, recovery: false, expectedLabelRevision, ct);

        EnsureLocalStorage(session.Project);

        var file = await Files.Find(new BsonDocument
        {
            ["_id"] = fileId,
            ["p"] = projectId
        }).FirstOrDefaultAsync(ct) ?? throw FileNotExist();

        if (file.Integer("t") != FileTypeImage) throw FileTypeNotSupport();

        var saveName = file.Text("sa");
        var runId = Guid.NewGuid().ToString("N");
        var now = _clock.GetUtcNow().UtcDateTime;

        var payload = new BsonDocument
        {
            ["file_id"] = fileId.ToString(),
            ["project_id"] = projectId.ToString(),
            ["label_revision"] = session.Revision,
            ["save_name"] = saveName,
            ["run_id"] = runId
        };

        // Make the durable job exist before advertising QUEUING. A crash after
        // this point is recoverable: the worker re-reads the file and carries
        // the same run id, while an enqueue failure leaves the previous honest
        // status untouched instead of a fake QUEUING row.
        await _queue.EnqueueAsync(JobKind, payload, ct);

        // The request holds the borrowed label lease, so the worker cannot
        // advance to GENERATING between the enqueue and this state write.
        var stateFilter = new BsonDocument
        {
            ["_id"] = fileId,
            ["p"] = projectId,
            ["sa"] = saveName
        };
        var state = Builders<BsonDocument>.Update
            .Set("th", ThumbnailQueuing)
            .Set("the", string.Empty)
            .Set("tht", now)
            .Unset("thumbnail_run")
            .Unset("thumbnail_run_at");
        var changed = await Files.UpdateOneAsync(stateFilter, state, cancellationToken: ct);
        if (changed.MatchedCount == 0) throw LabelErrors.Stale("文件已更新，无法排队缩略图任务");

        return true;
    }

    /// <summary>
    /// Schedules every activated image in the project. The query is a bounded
    /// cursor (no in-memory project file list) and each id is re-read under the
    /// request's borrowed label lease.
    /// </summary>
    public async Task<int> ScheduleProjectAsync(ObjectId projectId, CancellationToken ct)
    {
        if (!LocalGenerationRequired) return 0;

        // Check the storage pool before walking the cursor. Otherwise a pooled
        // project would enqueue a prefix of its images and then throw.
        var project = await Projects.Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct)
            ?? throw new ApiError("ProjectNotExistError", 4001, "项目不存在");
        EnsureLocalStorage(project);

        var filter = new BsonDocument
        {
            ["p"] = projectId,
            ["t"] = FileTypeImage,
            ["ac"] = true
        };
        var projection = new BsonDocument("_id", 1);

        var count = 0;
        using var cursor = await Files.Find(filter).Project(projection).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var row in cursor.Current)
            {
                await ScheduleAsync(row["_id"].AsObjectId, projectId, null, ct);
                count++;
            }
        }

        return count;
    }

    public async Task RunAsync(BsonDocument payload, CancellationToken ct) =>
        await RunCoreAsync(payload, attemptOverride: null, ct);

    /// <summary>
    /// Test/diagnostic seam. The production worker never passes an attempt
    /// count because JobQueue does not hand the job id to handlers.
    /// </summary>
    public async Task RunAsync(BsonDocument payload, int attempt, CancellationToken ct) =>
        await RunCoreAsync(payload, attempt, ct);

    private static void EnsureLocalStorage(BsonDocument project)
    {
        if (HasStoragePool(project)) throw StoragePoolUnsupported();
    }

    private static bool HasStoragePool(BsonDocument project) =>
        project.Contains("csid")
        && !project["csid"].IsBsonNull
        && project["csid"].ToJson().Trim('"').Length > 0;

    private static string Truncate(string message) =>
        message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];

    private static long RoundKb(long coverBytes, long resampleBytes) =>
        (long)Math.Round((coverBytes + resampleBytes) / 1024d, MidpointRounding.ToEven);

    private static DateTime? Date(BsonDocument document, string key) =>
        document.TryGetValue(key, out var value) && value.IsBsonDateTime
            ? value.ToUniversalTime()
            : null;

    private static string Stem(string saveName)
    {
        var dot = saveName.LastIndexOf('.');
        return dot >= 0 ? saveName[..dot] : saveName;
    }

    private static string FinalCoverKey(string saveName) => $"files/cover-{Stem(saveName)}.webp";

    private static string FinalResampleKey(string saveName) => $"files/resample-{Stem(saveName)}.webp";

    private static string StagingCoverKey(string runId) =>
        $"files/.thumbnail-staging/{runId}/cover.webp";

    private static string StagingResampleKey(string runId) =>
        $"files/.thumbnail-staging/{runId}/resample.webp";
}
