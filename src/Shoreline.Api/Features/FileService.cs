using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

public sealed record FileListQuery(
    string? ParentId,
    bool OnlyFolder,
    bool OnlyFile,
    IReadOnlyList<string> OrderBy,
    string? Word,
    string? TargetId);

public sealed record FileOrderView(
    IReadOnlyList<Dictionary<string, object?>> Files,
    string Version,
    IReadOnlyList<string> DefaultFileIds);

public sealed record ParsedFilename(
    string Name,
    string Prefix,
    string Suffix,
    int Type,
    string SortName);

public sealed partial class FileService
{
    public const string FilePrefix = "files/";

    public const int TypeUnknown = 0;
    public const int TypeFolder = 1;
    public const int TypeImage = 2;
    public const int TypeText = 3;

    public const int FileNotExistUnknown = 0;
    public const int FileNotExistNotUpload = 1;

    public const int ThumbnailUnknown = 0;
    public const int ThumbnailQueuing = 1;
    public const int ThumbnailGenerating = 2;
    public const int ThumbnailSucceeded = 3;
    public const int ThumbnailFailed = 4;

    private static readonly TimeSpan UrlValidity = TimeSpan.FromHours(1);
    private static readonly TimeSpan OrderLease = TimeSpan.FromMinutes(5);
    private static readonly char[] InvalidFilenameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];
    private static readonly Regex SortNameParts = new(@"\d+|\D+", RegexOptions.Compiled);
    private static readonly Regex NumericPart = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly string[][] FileOrderIndexes =
    [
        ["activated", "name", "parent", "project"],
        ["type", "sort_name"],
        ["project", "activated", "parent", "dir_sort_name", "type", "manual_order", "sort_name", "id"],
        ["type", "-sort_name"],
        ["dir_sort_name", "type", "sort_name"],
        ["dir_sort_name", "type", "-sort_name"],
        ["-edit_time"],
        ["project", "activated", "parent", "dir_sort_name", "type", "sort_name"],
    ];

    private static readonly HashSet<string> AllowedOrderSequences = BuildOrderSequences();

    private readonly MongoStore _store;
    private readonly ProjectService _projects;
    private readonly IdentityPermissions _permissions;
    private readonly ObjectStorage _storage;
    private readonly ProjectStorageRouter? _router;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _clock;
    private readonly FileMutationGate _gate;
    private readonly ProjectLabelLock _labels;
    private readonly LabelRequestContext _labelContext;
    private readonly IThumbnailScheduler? _thumbnails;
    private readonly ILogger<FileService>? _logger;

    private readonly IMongoCollection<BsonDocument> _files;
    private readonly IMongoCollection<BsonDocument> _caches;
    private readonly IMongoCollection<BsonDocument> _projectDocuments;
    private readonly IMongoCollection<BsonDocument> _targetDocuments;
    private readonly IMongoCollection<BsonDocument> _projectSetDocuments;
    private readonly IMongoCollection<BsonDocument> _teamDocuments;
    private readonly IMongoCollection<BsonDocument> _sourceDocuments;
    private readonly IMongoCollection<BsonDocument> _translationDocuments;
    private readonly IMongoCollection<BsonDocument> _tipDocuments;

    public FileService(
        MongoStore store,
        ProjectService projects,
        IdentityPermissions permissions,
        ObjectStorage storage,
        IConfiguration configuration,
        TimeProvider clock,
        FileMutationGate gate,
        ILogger<FileService>? logger = null,
        ProjectLabelLock? labelLock = null,
        LabelRequestContext? labelContext = null,
        ProjectStorageRouter? router = null,
        IThumbnailScheduler? thumbnails = null)
    {
        _store = store;
        _projects = projects;
        _permissions = permissions;
        _storage = storage;
        _router = router;
        _configuration = configuration;
        _clock = clock;
        _gate = gate;
        _labels = labelLock ?? new ProjectLabelLock(store, clock);
        _labelContext = labelContext ?? new LabelRequestContext();
        _thumbnails = thumbnails;
        _logger = logger;
        _files = store.Collection("file");
        _caches = store.Collection("file_target_cache");
        _projectDocuments = store.Collection("project");
        _targetDocuments = store.Collection("target");
        _projectSetDocuments = store.Collection("project_set");
        _teamDocuments = store.Collection("team");
        _sourceDocuments = store.Collection("source");
        _translationDocuments = store.Collection("translation");
        _tipDocuments = store.Collection("tip");
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>
    /// Test-only fault-injection seam, invoked after the upload metadata write
    /// has committed and before cache/edit-time updates run.  Never set in
    /// production.
    /// </summary>
    public Action? AfterMetadataWriteForTests { get; set; }

    private Task<LabelSession> EnterLabelAsync(ObjectId projectId, CancellationToken ct) =>
        _labels.EnterAsync(_labelContext, projectId, recovery: false, expectedRevision: null, ct);

    private long MaxUploadBytes =>
        _configuration.GetValue<long?>("Upload:MaxBytes") ?? 1024L * 1024 * 1024;

    private bool AllowTextUpload =>
        _configuration.GetValue<bool?>("File:AllowText") == true;

    /// <summary>
    /// Creates the legacy file indexes.  Program.cs must call this after the
    /// existing MongoStore.InitializeAsync call.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Early C# builds used descriptive names for indexes that Python's
        // MongoEngine creates as `<field>_1`. MongoDB refuses the same key
        // under a second name, which broke a rollback to Python, so migrate
        // the old names first. Python-created deployments already have the
        // default names and are left untouched.
        foreach (var legacy in new[]
                 {
                     "file_activated_name_parent_project", "file_type_sort_name",
                     "file_type_sort_name_desc", "file_dir_type_sort_name",
                     "file_dir_type_sort_name_desc", "file_target_cache_file_target",
                     "t_1_-sn_1", "dn_1_t_1_-sn_1",
                 })
        {
            await DropIndexIfExistsAsync(_files, legacy, ct);
            await DropIndexIfExistsAsync(_caches, legacy, ct);
        }

        await _files.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("ac").Ascending("n").Ascending("f").Ascending("p"),
                new CreateIndexOptions { Name = "ac_1_n_1_f_1_p_1" }),
            cancellationToken: ct);

        await _files.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("t").Ascending("sn"),
                new CreateIndexOptions { Name = "t_1_sn_1" }),
            cancellationToken: ct);

        await _files.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("p").Ascending("ac").Ascending("f").Ascending("dn")
                    .Ascending("t").Ascending("mo").Ascending("sn").Ascending("_id"),
                new CreateIndexOptions { Name = "file_manual_order_v1" }),
            cancellationToken: ct);

        await _files.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("t").Descending("sn"),
                new CreateIndexOptions { Name = "t_1_sn_-1" }),
            cancellationToken: ct);

        await _files.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("dn").Ascending("t").Ascending("sn"),
                new CreateIndexOptions { Name = "dn_1_t_1_sn_1" }),
            cancellationToken: ct);

        await _files.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("dn").Ascending("t").Descending("sn"),
                new CreateIndexOptions { Name = "dn_1_t_1_sn_-1" }),
            cancellationToken: ct);

        await _files.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Descending("et"),
                new CreateIndexOptions { Name = "file_admin_edit_time_v1" }),
            cancellationToken: ct);

        await _files.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("p").Ascending("ac").Ascending("f").Ascending("dn")
                    .Ascending("t").Ascending("sn"),
                new CreateIndexOptions { Name = "file_list_v2" }),
            cancellationToken: ct);

        await EnsureCacheIndexAsync(ct);
    }

    private async Task EnsureCacheIndexAsync(CancellationToken ct)
    {
        using var cursor = await _caches.Indexes.ListAsync(ct);
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var index in cursor.Current)
            {
                if (index.TryGetValue("key", out var key) && key.IsBsonDocument)
                {
                    var spec = key.AsBsonDocument;
                    if (spec.ElementCount == 2
                        && spec.TryGetValue("f", out var f) && f.ToInt32() == 1
                        && spec.TryGetValue("t", out var t) && t.ToInt32() == 1)
                    {
                        return;
                    }
                }
            }
        }

        await _caches.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("f").Ascending("t"),
                new CreateIndexOptions { Name = "f_1_t_1" }),
            cancellationToken: ct);
    }

    private static async Task DropIndexIfExistsAsync(
        IMongoCollection<BsonDocument> collection, string name, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await collection.Indexes.DropOneAsync(name, ct);
                return;
            }
            catch (MongoCommandException ex) when (ex.Code is 26 or 27)
            {
                // NamespaceNotFound / IndexNotFound: nothing to migrate.
                return;
            }
            catch (MongoCommandException ex) when (ex.Code == 125 && attempt < 5)
            {
                // BackgroundOperationInProgress: retry after the current build.
                await Task.Delay(50, ct);
            }
        }
    }

    // ---------------------------------------------------------------- queries

    public async Task<BsonDocument> GetFileAsync(string fileId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(fileId, out var id))
        {
            throw new ApiError("FileNotExistError", 8001, "文件/文件夹不存在，可能已被删除");
        }

        var file = await _files.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstOrDefaultAsync(ct);
        if (file is null)
        {
            throw new ApiError("FileNotExistError", 8001, "文件/文件夹不存在，可能已被删除");
        }

        return file;
    }

    public async Task<BsonDocument?> ResolveFolderAsync(
        BsonDocument project,
        string? folderId,
        CancellationToken ct)
    {
        if (folderId is null)
        {
            return null;
        }

        if (!ObjectId.TryParse(folderId, out var id))
        {
            throw new ApiError("InvalidObjectIdError", 101, "错误的ObjectId");
        }

        var folder = await _files.Find(
                Builders<BsonDocument>.Filter.Eq("_id", id)
                & Builders<BsonDocument>.Filter.Eq("p", project["_id"].AsObjectId)
                & Builders<BsonDocument>.Filter.Eq("t", TypeFolder))
            .FirstOrDefaultAsync(ct);

        if (folder is null)
        {
            throw new ApiError("FolderNotExistError", 8002, "文件夹不存在，可能已被删除");
        }

        return folder;
    }

    public async Task<(IReadOnlyList<Dictionary<string, object?>> Items, long Count)> ListAsync(
        BsonDocument project,
        FileListQuery query,
        Pagination page,
        CancellationToken ct)
    {
        EnsureWorking(project);
        if (query.OnlyFolder && query.OnlyFile)
        {
            throw ApiError.Invalid("can not use `only_file` and `only_folder` at one time");
        }

        var projectId = project["_id"].AsObjectId;
        var filters = new List<FilterDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Filter.Eq("p", projectId),
            Builders<BsonDocument>.Filter.Eq("ac", true),
        };

        var parent = await ResolveFolderAsync(project, query.ParentId, ct);
        filters.Add(parent is null
            ? Builders<BsonDocument>.Filter.Eq("f", BsonNull.Value)
            : Builders<BsonDocument>.Filter.Eq("f", parent["_id"].AsObjectId));

        if (query.OnlyFolder)
        {
            filters.Add(Builders<BsonDocument>.Filter.Eq("t", TypeFolder));
        }
        else if (query.OnlyFile)
        {
            filters.Add(Builders<BsonDocument>.Filter.Ne("t", TypeFolder));
        }

        if (!string.IsNullOrWhiteSpace(query.Word))
        {
            filters.Add(Builders<BsonDocument>.Filter.Regex(
                "n",
                new BsonRegularExpression(Regex.Escape(query.Word.Trim()), "i")));
        }

        ObjectId? targetId = null;
        if (!string.IsNullOrEmpty(query.TargetId))
        {
            if (!ObjectId.TryParse(query.TargetId, out var parsedTarget))
            {
                throw new ApiError("InvalidObjectIdError", 101, "错误的ObjectId");
            }

            var target = await _projects.GetTargetAsync(query.TargetId, ct);
            if (target["t"].AsObjectId != projectId)
            {
                throw new ApiError("TargetNotExistError", 4024, "目标语言不存在");
            }

            targetId = parsedTarget;
        }

        var filter = Builders<BsonDocument>.Filter.And(filters);
        var sort = await BuildListSortAsync(filter, query.OrderBy, ct);
        var count = await _files.CountDocumentsAsync(filter, cancellationToken: ct);

        var files = await _files.Find(filter)
            .Sort(sort)
            .Skip(page.Skip)
            .Limit(page.Limit)
            .ToListAsync(ct);

        Dictionary<ObjectId, BsonDocument> caches = [];
        if (targetId.HasValue && files.Count > 0)
        {
            var ids = files.Select(f => f["_id"].AsObjectId).ToList();
            var cacheDocuments = await _caches.Find(
                    Builders<BsonDocument>.Filter.In("f", ids)
                    & Builders<BsonDocument>.Filter.Eq("t", targetId.Value))
                .ToListAsync(ct);
            foreach (var cache in cacheDocuments)
            {
                caches[cache["f"].AsObjectId] = cache;
            }
        }

        var items = new List<Dictionary<string, object?>>(files.Count);
        foreach (var file in files)
        {
            var api = await ToApiAsync(file, project, ct);
            if (targetId.HasValue)
            {
                api["file_target_cache"] = caches.TryGetValue(file["_id"].AsObjectId, out var cache)
                    ? CacheApi(cache)
                    : EmptyCacheApi();
            }

            items.Add(api);
        }

        return (items, count);
    }

    private async Task<SortDefinition<BsonDocument>> BuildListSortAsync(
        FilterDefinition<BsonDocument> filter,
        IReadOnlyList<string> orderBy,
        CancellationToken ct)
    {
        var sort = Builders<BsonDocument>.Sort;
        if (orderBy.Count == 0)
        {
            var manual = await _files.Find(
                    filter & Builders<BsonDocument>.Filter.Ne("mo", BsonNull.Value))
                .Project(Builders<BsonDocument>.Projection.Include("_id"))
                .Limit(1)
                .FirstOrDefaultAsync(ct);

            return manual is null
                ? sort.Ascending("dn").Ascending("t").Ascending("sn")
                : sort.Ascending("dn").Ascending("t").Ascending("mo").Ascending("sn").Ascending("_id");
        }

        if (!AllowedOrderSequences.Contains(string.Join('\u0001', orderBy)))
        {
            throw ApiError.Invalid($"不支持使用 [{string.Join(", ", orderBy.Select(item => $"'{item}'"))}] 排序");
        }

        SortDefinition<BsonDocument>? definition = null;
        foreach (var raw in orderBy)
        {
            var descending = raw.StartsWith('-');
            var field = descending ? raw[1..] : raw;
            var dbField = OrderField(field)
                ?? throw ApiError.Invalid($"不支持使用 {raw} 排序");
            var next = descending ? sort.Descending(dbField) : sort.Ascending(dbField);
            definition = definition is null ? next : sort.Combine(definition, next);
        }

        return definition ?? sort.Ascending("dn").Ascending("t").Ascending("sn");
    }

    private static HashSet<string> BuildOrderSequences()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in FileOrderIndexes)
        {
            var negated = index.Select(FlipOrderToken).ToArray();
            foreach (var sequence in new[] { index, negated })
            {
                for (var length = 1; length <= sequence.Length; length++)
                {
                    allowed.Add(string.Join('\u0001', sequence.Take(length)));
                }
            }
        }

        return allowed;
    }

    private static string FlipOrderToken(string token)
        => token.StartsWith('-') ? token[1..] : "-" + token;

    private static string? OrderField(string field) => field switch
    {
        "id" => "_id",
        "name" => "n",
        "type" => "t",
        "project" => "p",
        "parent" => "f",
        "manual_order" => "mo",
        "sort_name" => "sn",
        "dir_sort_name" => "dn",
        "edit_time" => "et",
        "activated" => "ac",
        _ => null,
    };

    public async Task<Dictionary<string, object?>> GetAsync(
        BsonDocument file,
        BsonDocument project,
        string? targetId,
        CancellationToken ct)
    {
        var data = await ToApiAsync(file, project, ct);
        data["project_id"] = project["_id"].AsObjectId.ToString();

        if (!string.IsNullOrEmpty(targetId))
        {
            if (!ObjectId.TryParse(targetId, out var parsedTarget))
            {
                throw new ApiError("InvalidObjectIdError", 101, "错误的ObjectId");
            }

            var target = await _projects.GetTargetAsync(targetId, ct);
            if (target["t"].AsObjectId != project["_id"].AsObjectId)
            {
                throw new ApiError("TargetNotExistError", 4024, "目标语言不存在");
            }

            var cache = await _caches.Find(
                    Builders<BsonDocument>.Filter.Eq("f", file["_id"].AsObjectId)
                    & Builders<BsonDocument>.Filter.Eq("t", parsedTarget))
                .FirstOrDefaultAsync(ct);
            data["file_target_cache"] = cache is null ? EmptyCacheApi() : CacheApi(cache);
        }

        if (file.Integer("t") == TypeImage)
        {
            var (previous, next) = await NeighborsAsync(file, ct);
            if (previous is not null)
            {
                data["prev_image"] = await ToApiAsync(previous, project, ct);
            }

            if (next is not null)
            {
                data["next_image"] = await ToApiAsync(next, project, ct);
            }
        }

        return data;
    }

    // ---------------------------------------------------------------- upload

    public async Task<Dictionary<string, object?>> UploadAsync(
        BsonDocument project,
        string filename,
        IFormFile upload,
        string? parentId,
        CancellationToken ct)
    {
        EnsureWorking(project);
        var projectId = project["_id"].AsObjectId;

        await using var labelSession = await EnterLabelAsync(projectId, ct);
        using var _ = await _gate.EnterAsync(projectId, ct);
        // Reload under the locks: another request may have changed the project.
        project = await _projects.GetProjectAsync(projectId.ToString(), ct);
        await using var storageLease = await LeaseForProjectAsync(project, ct);
        var storage = storageLease?.Backend ?? _storage;

        var parsed = ParseFilename(filename, folder: false);
        if (parsed.Type != TypeImage && !(parsed.Type == TypeText && AllowTextUpload))
        {
            throw FilenameError("暂不支持的文件格式");
        }

        var parent = await ResolveFolderAsync(project, parentId, ct);
        var old = await FindByNameAsync(projectId, parent, filename, activated: true, ct);
        BsonDocument file;
        var isNew = false;
        var isRevision = false;

        if (old is null)
        {
            file = await CreateFileDocumentAsync(project, parent, parsed, labelSession.Revision, ct);
            isNew = true;
        }
        else if (old.Integer("t") == TypeImage)
        {
            // Legacy overwrite behaviour: same name keeps the same file id.
            labelSession.AssertReferenceEpoch(old);
            file = old;
        }
        else if (old.Integer("t") == TypeText)
        {
            labelSession.AssertReferenceEpoch(old);
            file = await CreateRevisionAsync(project, old, labelSession.Revision, ct);
            isRevision = true;
        }
        else
        {
            throw new ApiError("FilenameDuplicateError", 4007, "文件名重复");
        }

        // Python overwrite keeps the existing document (and its name); the new
        // object key therefore uses the existing suffix, not the upload's one.
        var saveSuffix = old is not null
            ? SuffixOf(old.Text("n"))
            : parsed.Suffix;
        var saveName = ObjectId.GenerateNewId().ToString() + "." + saveSuffix;
        var oldSaveName = file.Text("sa");
        var oldSize = file.Contains("fs") && !file["fs"].IsBsonNull ? file["fs"].ToInt64() : 0L;
        var tempPath = Path.Combine(OpenCodeTempDirectory(), "moeflow-" + Guid.NewGuid().ToString("N") + "-" + saveName);
        string md5;
        long byteLength;

        var metadataPersisted = false;
        try
        {
            (md5, byteLength) = await WriteTempAsync(upload, tempPath, ct);
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await storage.PutAsync(
                    FilePrefix + saveName,
                    stream,
                    string.IsNullOrWhiteSpace(upload.ContentType) ? "application/octet-stream" : upload.ContentType,
                    ct);
            }

            // From here on the object is the pending target of the metadata
            // write. A failed or ambiguous Mongo update must never delete it:
            // the update may have committed even when the driver reports an
            // error (network/unknown), and deleting would leave file.sa
            // pointing at a missing object.
            metadataPersisted = true;

            var newSize = (byteLength + 1023) / 1024;
            await _files.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", file["_id"].AsObjectId),
                Builders<BsonDocument>.Update
                    .Set("sa", saveName)
                    .Set("md", md5)
                    .Set("fn", FileNotExistUnknown),
                cancellationToken: ct);

            // Test-only seam: simulates a cache/touch failure after the
            // metadata write actually succeeded.
            AfterMetadataWriteForTests?.Invoke();

            await IncFileChainAsync(
                file, "fs", newSize - oldSize, includeSelf: true, updateProject: true, isLong: true, ct);

            file["sa"] = saveName;
            file["md"] = md5;
            file["fn"] = FileNotExistUnknown;
            file["fs"] = new BsonInt64(newSize);
            await TouchEditTimeAsync(project, file, ct);

            if (!string.IsNullOrEmpty(oldSaveName) && oldSaveName != saveName)
            {
                await DeleteStoredObjectsBestEffortAsync(oldSaveName, storage);
            }

            // Python File.upload_real_file schedules local thumbnails right
            // after the rejected-upload/old-object work (app/models/file.py:867-891).
            // The scheduler no-ops in server-side/Alibaba mode, so this branch
            // only needs the image-type check. A scheduling failure must never
            // fail an already-persisted upload; leave the previous honest
            // status instead of advertising QUEUING.
            if (_thumbnails is not null && file.Integer("t") == TypeImage)
            {
                try
                {
                    await _thumbnails.ScheduleAsync(
                        file["_id"].AsObjectId, projectId, labelSession.Revision, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger?.LogWarning(
                        "Thumbnail schedule after upload failed ({ErrorType})",
                        exception.GetType().Name);
                }
            }
        }
        catch
        {
            if (!metadataPersisted)
            {
                // Known-before-persist failure: the metadata still points at
                // the old object, so removing the new orphan is safe.
                await BestEffortDeleteAsync(FilePrefix + saveName, storage);
            }

            throw;
        }
        finally
        {
            TryDeleteTemp(tempPath);
        }

        if (isNew)
        {
            // create_file already counted it; the upload only adjusts file_size.
            _logger?.LogDebug("Created file {FileId}", file["_id"]);
        }

        if (isRevision)
        {
            _logger?.LogDebug("Uploaded new revision {FileId}", file["_id"]);
        }

        var data = await ToApiAsync(file, project, ct);
        data["upload_overwrite"] = old is not null;
        return data;
    }

    private async Task<BsonDocument> CreateFileDocumentAsync(
        BsonDocument project,
        BsonDocument? parent,
        ParsedFilename parsed,
        long labelRevision,
        CancellationToken ct)
    {
        var projectId = project["_id"].AsObjectId;
        var now = Now.UtcDateTime;
        var ancestors = parent is null
            ? new BsonArray()
            : new BsonArray(parent["a"].AsBsonArray.Append(parent["_id"].AsObjectId));
        var dirSortName = parent is null ? "" : parent.Text("dn") + parent.Text("sn") + "/";
        var manualOrder = await LastManualOrderAsync(projectId, parent, parsed.Type, ct);

        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = parsed.Name,
            ["t"] = parsed.Type,
            ["p"] = projectId,
            ["f"] = parent is null ? BsonNull.Value : parent["_id"].AsObjectId,
            ["a"] = ancestors,
            ["mo"] = manualOrder.HasValue ? new BsonInt32(manualOrder.Value + 1) : BsonNull.Value,
            ["sn"] = parsed.SortName,
            ["dn"] = dirSortName,
            ["sa"] = "",
            ["md"] = "",
            ["fs"] = new BsonInt64(0),
            ["cover_bytes"] = new BsonInt64(0),
            ["resample_bytes"] = new BsonInt64(0),
            ["fn"] = FileNotExistNotUpload,
            ["th"] = ThumbnailUnknown,
            ["the"] = "",
            ["fo"] = 0,
            ["fc"] = 0,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["et"] = new BsonDateTime(now),
            ["ov"] = BsonNull.Value,
            ["v"] = 1,
            ["ac"] = true,
            ["ss"] = 0,
            ["sti"] = 0,
            ["sri"] = 0,
            ["sst"] = 0,
            ["ps"] = 0,
            ["pt"] = new BsonArray(),
            ["pti"] = "",
            ["pst"] = BsonNull.Value,
            ["pe"] = 0,
            ["op"] = 0,
            ["ft"] = 0,
            ["ftt"] = 0,
            ["fts"] = "",
            ["sm"] = 0,
            ["label_epoch"] = new BsonInt64(labelRevision),
        };

        await _files.InsertOneAsync(document, cancellationToken: ct);
        await CreateTargetCachesAsync(document["_id"].AsObjectId, projectId, ct);
        await IncFileChainAsync(document, "fc", 1, includeSelf: false, updateProject: true, isLong: false, ct);
        return document;
    }

    private async Task<int?> LastManualOrderAsync(
        ObjectId projectId,
        BsonDocument? parent,
        long type,
        CancellationToken ct)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("p", projectId)
                     & Builders<BsonDocument>.Filter.Eq("t", type)
                     & Builders<BsonDocument>.Filter.Eq("ac", true)
                     & Builders<BsonDocument>.Filter.Ne("mo", BsonNull.Value);
        filter &= parent is null
            ? Builders<BsonDocument>.Filter.Eq("f", BsonNull.Value)
            : Builders<BsonDocument>.Filter.Eq("f", parent["_id"].AsObjectId);

        var last = await _files.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("mo"))
            .Limit(1)
            .FirstOrDefaultAsync(ct);
        return last is null ? null : last["mo"].ToInt32();
    }

    private async Task CreateTargetCachesAsync(ObjectId fileId, ObjectId projectId, CancellationToken ct)
    {
        var targets = await _targetDocuments.Find(
                Builders<BsonDocument>.Filter.Eq("t", projectId))
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .ToListAsync(ct);

        if (targets.Count == 0)
        {
            return;
        }

        var now = new BsonDateTime(Now.UtcDateTime);
        var documents = targets.Select(target => new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["f"] = fileId,
            ["t"] = target["_id"].AsObjectId,
            ["ts"] = 0,
            ["cs"] = 0,
            ["e"] = now,
        }).ToList();

        await _caches.InsertManyAsync(documents, cancellationToken: ct);
    }

    private async Task<(string Md5, long Length)> WriteTempAsync(
        IFormFile upload,
        string path,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var destination = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var source = upload.OpenReadStream();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaxUploadBytes)
            {
                throw new ApiError("RequestEntityTooLarge", 2, "上传文件超过大小限制", 413);
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        await destination.FlushAsync(ct);
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), total);
    }

    private static string OpenCodeTempDirectory()
        => Path.Combine(Path.GetTempPath(), "opencode");

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort; the task-owned temp file must not fail the request.
        }
    }

    private async Task BestEffortDeleteAsync(string key, IStorageBackend storage,
        CancellationToken ct = default)
    {
        try
        {
            await storage.DeleteAsync(key, ct);
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "Failed to clean up object {Key}", key);
        }
    }

    private async Task DeleteStoredObjectsBestEffortAsync(string saveName, IStorageBackend storage)
    {
        // Old-object cleanup runs after the metadata commit.  It is detached
        // from the request token (client may already have gone), but must not
        // hang forever on a wedged backend.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var key in StoredObjectKeys(saveName))
        {
            await BestEffortDeleteAsync(key, storage, cleanup.Token);
        }
    }

    private static IEnumerable<string> StoredObjectKeys(string saveName)
    {
        var stem = saveName;
        var dot = saveName.LastIndexOf('.');
        if (dot >= 0)
        {
            stem = saveName[..dot];
        }

        yield return FilePrefix + saveName;
        yield return FilePrefix + "cover-" + stem + ".webp";
        yield return FilePrefix + "resample-" + stem + ".webp";
    }

    // ------------------------------------------------------------- filenames

    public static ParsedFilename ParseFilename(string name, bool folder)
    {
        var noSpaces = name.Replace(" ", "");
        if (noSpaces.Length == 0)
        {
            throw FilenameError("文件名为空");
        }

        if (name.EnumerateRunes().Count() > 127)
        {
            throw FilenameError("文件名过长");
        }

        if (noSpaces is "." or "..")
        {
            throw FilenameError("文件名不能是 . 或 ..");
        }

        if (noSpaces.EndsWith('.'))
        {
            throw FilenameError("文件名不能以 . 结尾");
        }

        var dot = name.LastIndexOf('.');
        var prefix = dot >= 0 ? name[..dot] : name;
        var suffix = dot >= 0 ? name[(dot + 1)..] : "";
        if (prefix.Replace(" ", "").Length == 0)
        {
            throw FilenameError("文件名前缀为空");
        }

        if (name.IndexOfAny(InvalidFilenameChars) >= 0)
        {
            throw FilenameError("文件名不能包含下列任何字符: \\ / : * ? \" < > |");
        }

        if (folder)
        {
            return new ParsedFilename(name, name, "", TypeFolder, BuildSortName(name));
        }

        return new ParsedFilename(name, prefix, suffix, TypeBySuffix(suffix), BuildSortName(prefix));
    }

    private static ApiError FilenameError(string detail)
        => new("FilenameIllegalError", 107, "文件名错误: " + detail);

    private static string SuffixOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : "";
    }

    public static int TypeBySuffix(string suffix) => suffix.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" or "png" or "bmp" or "gif" or "webp" => TypeImage,
        "txt" => TypeText,
        _ => TypeUnknown,
    };

    private static string BuildSortName(string prefix)
    {
        var sb = new StringBuilder();
        foreach (Match match in SortNameParts.Matches(prefix))
        {
            var part = match.Value;
            if (NumericPart.IsMatch(part) && part.Length < 6)
            {
                sb.Append('0', 6 - part.Length);
            }

            sb.Append(part);
        }

        return sb.ToString();
    }

    // --------------------------------------------------------------- helpers

    private static void EnsureWorking(BsonDocument project)
    {
        if (project.Integer("st") != 0)
        {
            throw new ApiError("ProjectFinishedError", 4002, "项目已完结");
        }
    }

    private async Task<StorageBackendLease?> LeaseForProjectAsync(
        BsonDocument? project,
        CancellationToken ct)
    {
        if (project is null || !HasStoragePool(project)) return null;
        if (_router is null) throw ProjectStorageRouter.SourceUnavailable();
        return await _router.ForProjectAsync(project, ct);
    }

    private async Task<BsonDocument?> FindByNameAsync(
        ObjectId projectId,
        BsonDocument? parent,
        string name,
        bool activated,
        CancellationToken ct)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("p", projectId)
                     & Builders<BsonDocument>.Filter.Regex(
                         "n",
                         new BsonRegularExpression("^" + Regex.Escape(name) + "$", "i"))
                     & Builders<BsonDocument>.Filter.Eq("ac", activated);
        filter &= parent is null
            ? Builders<BsonDocument>.Filter.Eq("f", BsonNull.Value)
            : Builders<BsonDocument>.Filter.Eq("f", parent["_id"].AsObjectId);
        return await _files.Find(filter).FirstOrDefaultAsync(ct);
    }

    private static BsonArray AncestorsOf(BsonDocument? parent)
        => parent is null
            ? new BsonArray()
            : new BsonArray(parent["a"].AsBsonArray.Append(parent["_id"].AsObjectId));

    private static BsonValue ParentValue(BsonDocument? parent)
        => parent is null ? BsonNull.Value : parent["_id"].AsObjectId;

    private static BsonArray AncestorIds(BsonDocument file)
        => file.Contains("a") ? file["a"].AsBsonArray : new BsonArray();

    private static long ToLong(BsonValue value)
        => value.IsBsonNull ? 0 : value.ToInt64();

    private static int ToInt(BsonValue value)
        => value.IsBsonNull ? 0 : value.ToInt32();
}
