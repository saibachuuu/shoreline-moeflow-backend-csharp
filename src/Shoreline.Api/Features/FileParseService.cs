using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

/// <summary>
/// Direct-service port of <c>app/tasks/file_parse.py</c>. Python runs both tasks
/// through Celery with <c>time_limit=1200</c>. C# has no hard task timeout: the
/// shared <see cref="JobWorker"/> renews a two-minute lease every 30 seconds and
/// stops after the queue's five-attempt ceiling, so a single run may exceed
/// 1200 seconds while it keeps renewing. That is the documented lease/attempt
/// analogue, not a byte-for-byte timeout.
/// </summary>
public sealed partial class FileParseService(
    MongoStore store,
    ObjectStorage storage,
    JobQueue jobs,
    ProjectLabelLock labels,
    LabelRequestContext labelContext,
    TimeProvider clock,
    ILogger<FileParseService>? logger = null)
{
    public const string ParseTextJobKind = "parse_text";
    public const string FindTermsJobKind = "find_terms";

    public const int FileTypeText = 3;

    public const int ParseNotStart = 0;
    public const int ParseQueuing = 1;
    public const int ParseParsing = 2;
    public const int ParseFailed = 3;
    public const int ParseSucceeded = 4;

    public const int ParseErrorUnknown = 0;
    public const int ParseErrorUnknownCharset = 1;
    public const int ParseErrorFileCannotRead = 2;

    public const int FindTermsQueuing = 0;
    public const int FindTermsFinding = 1;
    public const int FindTermsFinished = 2;

    private static readonly Regex LineBreak = new(@"\n|\r\n|\r", RegexOptions.Compiled);

    private IMongoCollection<BsonDocument> Files => store.Collection("file");
    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private IMongoCollection<BsonDocument> Sources => store.Collection("source");
    private IMongoCollection<BsonDocument> Translations => store.Collection("translation");
    private IMongoCollection<BsonDocument> Tips => store.Collection("tip");
    private IMongoCollection<BsonDocument> Terms => store.Collection("term");
    private IMongoCollection<BsonDocument> TargetCaches => store.Collection("file_target_cache");

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Python <c>File.parse()</c> enqueue point for TEXT
    /// (app/models/file.py:1126-1145): clear existing sources, mark QUEUING,
    /// enqueue <c>parse_text</c>, then record the durable job id in
    /// <c>parse_task_id</c>. The durable job is written before the QUEUING
    /// advertisement so a failed enqueue leaves the previous status honest.
    /// </summary>
    public async Task<ObjectId> ScheduleParseTextAsync(
        ObjectId fileId,
        ObjectId? oldRevisionId,
        CancellationToken ct = default)
    {
        var projectId = await ProjectIdOfFileAsync(fileId, ct);
        await using var session = await labels.EnterAsync(labelContext, projectId, recovery: false,
            expectedRevision: null, ct);

        var file = await RequireTextFileAsync(fileId, ct);
        await ClearSourcesAsync(file, session, ct);

        var payload = new BsonDocument
        {
            ["file_id"] = fileId.ToString(),
            ["old_revision_id"] = oldRevisionId is { } old ? old.ToString() : BsonNull.Value,
            ["run_id"] = Guid.NewGuid().ToString("N")
        };
        var jobId = await jobs.EnqueueAsync(ParseTextJobKind, payload, ct);

        await Files.UpdateOneAsync(new BsonDocument("_id", fileId),
            new BsonDocument("$set", new BsonDocument
            {
                ["ps"] = ParseQueuing,
                ["pti"] = jobId.ToString()
            }), cancellationToken: ct);
        return jobId;
    }

    /// <summary>
    /// Python <c>parse_text(file_id, old_revision_id=..., run_sync=True/False)</c>
    /// (app/tasks/file_parse.py:109-116). The direct path is the test/admin
    /// <c>run_sync</c> call; the asynchronous path defers to
    /// <see cref="ScheduleParseTextAsync"/>.
    /// </summary>
    public async Task<ObjectId?> ParseTextAsync(
        ObjectId fileId,
        ObjectId? oldRevisionId,
        bool runSync,
        CancellationToken ct = default)
    {
        if (runSync)
        {
            await ScheduleDirectlyAsync(fileId, ParseTextJobKind, oldRevisionId, ct);
            await RunParseTextAsync(new BsonDocument
            {
                ["file_id"] = fileId.ToString(),
                ["old_revision_id"] = oldRevisionId is { } old ? old.ToString() : BsonNull.Value,
                ["run_id"] = Guid.NewGuid().ToString("N")
            }, ct);
            return null;
        }

        return await ScheduleParseTextAsync(fileId, oldRevisionId, ct);
    }

    /// <summary>
    /// Job body: <c>tasks.parse_text_task(file_id, old_revision_id=None)</c>.
    /// </summary>
    public async Task RunParseTextAsync(BsonDocument payload, CancellationToken ct = default)
    {
        if (!TryId(payload.GetValue("file_id", BsonNull.Value), out var fileId))
            return;
        ObjectId? oldRevisionId = TryId(payload.GetValue("old_revision_id", BsonNull.Value), out var oldId)
            ? oldId
            : null;
        var runId = payload.Text("run_id");
        var projectId = await ProjectIdOfFileOrNullAsync(fileId, ct);
        if (projectId is null) return;

        await using var session = await labels.EnterAsync(labelContext, projectId.Value, recovery: true,
            expectedRevision: null, ct);

        var oldRevision = oldRevisionId is { } oldValue
            ? await Files.Find(new BsonDocument { ["_id"] = oldValue, ["t"] = FileTypeText })
                .FirstOrDefaultAsync(ct)
            : null;

        var file = await Files.Find(new BsonDocument { ["_id"] = fileId, ["t"] = FileTypeText })
            .FirstOrDefaultAsync(ct);
        if (file is null) return;

        var status = file.Integer("ps");
        if (status == ParseSucceeded) return;
        if (status == ParseParsing && !SameRun(file, runId))
        {
            // Python skips any PARSING file (app/tasks/file_parse.py:53-54).
            // The C# queue may redeliver the same run after a lease expiry, so
            // only the identical run id may resume; a different producer still
            // observes the Python "already parsing" skip.
            return;
        }

        // A retry of our own run may have inserted a prefix of the sources
        // before the process died. Clear that partial state so the rerun is
        // idempotent; a first run is empty here.
        if (status == ParseParsing && SameRun(file, runId))
            await ClearSourcesAsync(file, session, ct);

        await Files.UpdateOneAsync(new BsonDocument("_id", fileId),
            new BsonDocument("$set", new BsonDocument
            {
                ["ps"] = ParseParsing,
                ["pst"] = Now,
                ["parse_run"] = runId
            }), cancellationToken: ct);

        byte[] bytes;
        try
        {
            var saveName = file.Text("sa");
            using var download = await storage.GetAsync(FileService.FilePrefix + saveName, ct);
            using var buffer = new MemoryStream();
            await download.Content.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not MongoDB.Driver.MongoException)
        {
            logger?.LogError("Text parse source could not be read ({ErrorType})", error.GetType().Name);
            await MarkParseFailedAsync(fileId, ParseErrorFileCannotRead, ct);
            return;
        }

        string text;
        try
        {
            text = DecodeText(bytes);
        }
        catch (Exception error) when (error is DecoderFallbackException or ArgumentException)
        {
            logger?.LogError("Text parse charset conversion failed ({ErrorType})", error.GetType().Name);
            await MarkParseFailedAsync(fileId, ParseErrorUnknownCharset, ct);
            return;
        }

        var lines = LineBreak.Split(text);
        var project = session.Project;
        var terms = await LoadProjectTermsAsync(project, ct);
        for (var rank = 0; rank < lines.Length; rank++)
        {
            var line = lines[rank];
            var blank = line.Trim().Length == 0;
            var source = new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["f"] = fileId,
                ["r"] = rank,
                ["c"] = line,
                ["x"] = 0.0,
                ["y"] = 0.0,
                ["v"] = new BsonArray(),
                ["p"] = 0,
                ["lf"] = true,
                ["b"] = blank,
                ["cm"] = true,
                ["pt"] = new BsonArray(),
                ["ct"] = Now,
                ["e"] = Now,
                ["label_epoch"] = session.Revision
            };
            await Sources.InsertOneAsync(source, cancellationToken: ct);

            if (!blank)
            {
                await AdjustCountAsync(file, session, "source_count", +1, null, ct);
                if (oldRevision is not null && oldRevision["_id"].AsObjectId != fileId)
                {
                    var oldSource = await Sources.Find(new BsonDocument
                    {
                        ["f"] = oldRevision["_id"].AsObjectId,
                        ["c"] = line
                    }).FirstOrDefaultAsync(ct);
                    if (oldSource is not null)
                        await CopySourceAsync(file, source, oldSource, session, ct);
                }

                await ApplyTermsAsync(source, terms, ct);
            }
        }

        await Files.UpdateOneAsync(new BsonDocument("_id", fileId),
            new BsonDocument
            {
                ["$set"] = new BsonDocument("ps", ParseSucceeded),
                ["$unset"] = new BsonDocument
                {
                    ["pt"] = 1,
                    ["pti"] = 1,
                    ["pst"] = 1,
                    ["parse_run"] = 1
                }
            }, cancellationToken: ct);
    }

    /// <summary>
    /// Python <c>Project.find_terms()</c> enqueue point
    /// (app/models/project.py:575-592): mark QUEUING after the durable job
    /// exists and record its id in <c>find_terms_task_id</c>.
    /// </summary>
    public async Task<ObjectId> ScheduleFindTermsAsync(ObjectId fileId, CancellationToken ct = default)
    {
        var payload = new BsonDocument
        {
            ["file_id"] = fileId.ToString(),
            ["run_id"] = Guid.NewGuid().ToString("N")
        };
        var jobId = await jobs.EnqueueAsync(FindTermsJobKind, payload, ct);

        await Files.UpdateOneAsync(new BsonDocument { ["_id"] = fileId, ["t"] = FileTypeText },
            new BsonDocument("$set", new BsonDocument
            {
                ["ft"] = FindTermsQueuing,
                ["ftt"] = jobId.ToString()
            }), cancellationToken: ct);
        return jobId;
    }

    /// <summary>
    /// Python <c>find_terms(file_id, run_sync=...)</c>
    /// (app/tasks/file_parse.py:159-166).
    /// </summary>
    public async Task<ObjectId?> FindTermsAsync(
        ObjectId fileId,
        bool runSync,
        CancellationToken ct = default)
    {
        if (!runSync) return await ScheduleFindTermsAsync(fileId, ct);

        await RunFindTermsAsync(new BsonDocument
        {
            ["file_id"] = fileId.ToString(),
            ["run_id"] = Guid.NewGuid().ToString("N")
        }, ct);
        return null;
    }

    /// <summary>Job body: <c>tasks.find_terms_task(file_id)</c>.</summary>
    public async Task RunFindTermsAsync(BsonDocument payload, CancellationToken ct = default)
    {
        if (!TryId(payload.GetValue("file_id", BsonNull.Value), out var fileId)) return;
        var runId = payload.Text("run_id");
        var file = await Files.Find(new BsonDocument { ["_id"] = fileId, ["t"] = FileTypeText })
            .FirstOrDefaultAsync(ct);
        if (file is null) return;

        if (file.Integer("ft") == FindTermsFinding && !SameRun(file, runId, "find_terms_run"))
            return;

        var projectId = ProjectIdOf(file);
        if (projectId is null) return;
        await using var session = await labels.EnterAsync(labelContext, projectId.Value, recovery: true,
            expectedRevision: null, ct);

        await Files.UpdateOneAsync(new BsonDocument("_id", fileId),
            new BsonDocument("$set", new BsonDocument
            {
                ["ft"] = FindTermsFinding,
                ["fts"] = Now,
                ["find_terms_run"] = runId
            }), cancellationToken: ct);

        var terms = await LoadProjectTermsAsync(session.Project, ct);
        using var sources = await Sources.Find(new BsonDocument("f", fileId))
            .Sort(new BsonDocument("r", 1))
            .ToCursorAsync(ct);
        while (await sources.MoveNextAsync(ct))
        {
            foreach (var source in sources.Current)
                await ApplyTermsAsync(source, terms, ct);
        }

        await Files.UpdateOneAsync(new BsonDocument("_id", fileId),
            new BsonDocument
            {
                ["$set"] = new BsonDocument("ft", FindTermsFinished),
                ["$unset"] = new BsonDocument
                {
                    ["ftt"] = 1,
                    ["fts"] = 1,
                    ["find_terms_run"] = 1
                }
            }, cancellationToken: ct);
    }

    private async Task ScheduleDirectlyAsync(
        ObjectId fileId,
        string kind,
        ObjectId? oldRevisionId,
        CancellationToken ct)
    {
        // Mirror File.parse: clear first, advertise QUEUING, then run in-process.
        var projectId = await ProjectIdOfFileAsync(fileId, ct);
        await using var session = await labels.EnterAsync(labelContext, projectId, recovery: false,
            expectedRevision: null, ct);
        await ClearSourcesAsync(await RequireTextFileAsync(fileId, ct), session, ct);
        await Files.UpdateOneAsync(new BsonDocument("_id", fileId),
            new BsonDocument("$set", new BsonDocument("ps", ParseQueuing)), cancellationToken: ct);
        _ = kind;
        _ = oldRevisionId;
    }

    private async Task MarkParseFailedAsync(ObjectId fileId, int errorType, CancellationToken ct) =>
        await Files.UpdateOneAsync(new BsonDocument("_id", fileId),
            new BsonDocument
            {
                ["$set"] = new BsonDocument
                {
                    ["ps"] = ParseFailed,
                    ["pe"] = errorType
                },
                ["$inc"] = new BsonDocument("pt", 1)
            }, cancellationToken: ct);

    private async Task ApplyTermsAsync(BsonDocument source, IReadOnlyList<BsonDocument> terms,
        CancellationToken ct)
    {
        var content = source.Text("c");
        var matched = new BsonArray();
        var seen = new HashSet<ObjectId>();
        foreach (var term in terms)
        {
            var id = term["_id"].AsObjectId;
            var value = term.Text("o");
            if (!content.Contains(value, StringComparison.Ordinal) || !seen.Add(id)) continue;
            matched.Add(id);
        }

        source["pt"] = matched;
        await Sources.UpdateOneAsync(new BsonDocument("_id", source["_id"]),
            new BsonDocument("$set", new BsonDocument("pt", matched)), cancellationToken: ct);
    }

    private async Task<IReadOnlyList<BsonDocument>> LoadProjectTermsAsync(BsonDocument project,
        CancellationToken ct)
    {
        var banks = new BsonArray();
        if (project.TryGetValue("tb", out var value) && value is BsonArray array)
        {
            foreach (var bank in array)
                if (bank.IsObjectId) banks.Add(bank.AsObjectId);
        }
        if (banks.Count == 0) return Array.Empty<BsonDocument>();
        return await Terms.Find(new BsonDocument("tb", new BsonDocument("$in", banks))).ToListAsync(ct);
    }

    private async Task CopySourceAsync(
        BsonDocument file,
        BsonDocument source,
        BsonDocument oldSource,
        LabelSession session,
        CancellationToken ct)
    {
        await Sources.UpdateOneAsync(new BsonDocument("_id", source["_id"]),
            new BsonDocument("$set", new BsonDocument
            {
                ["x"] = oldSource.GetValue("x", 0.0),
                ["y"] = oldSource.GetValue("y", 0.0),
                ["v"] = oldSource.GetValue("v", new BsonArray()),
                ["lf"] = oldSource.Flag("lf") || !oldSource.Contains("lf"),
                ["b"] = oldSource.Flag("b"),
                ["cm"] = oldSource.Flag("cm") || !oldSource.Contains("cm")
            }), cancellationToken: ct);
        source["x"] = oldSource.GetValue("x", 0.0);
        source["y"] = oldSource.GetValue("y", 0.0);
        source["v"] = oldSource.GetValue("v", new BsonArray());
        source["lf"] = oldSource.Flag("lf") || !oldSource.Contains("lf");
        source["b"] = oldSource.Flag("b");
        source["cm"] = oldSource.Flag("cm") || !oldSource.Contains("cm");

        var project = session.Project;
        var targets = await store.Collection("target")
            .Find(new BsonDocument("t", project["_id"]))
            .ToListAsync(ct);
        var translated = new HashSet<ObjectId>();
        var checkedTargets = new HashSet<ObjectId>();

        var oldTranslations = await Translations.Find(new BsonDocument("o", oldSource["_id"]))
            .ToListAsync(ct);
        foreach (var translation in oldTranslations)
        {
            if (!translation.TryGetValue("t", out var targetValue) || !targetValue.IsObjectId) continue;
            var targetId = targetValue.AsObjectId;
            var copy = new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["o"] = source["_id"],
                ["t"] = targetId,
                ["c"] = translation.Text("c"),
                ["u"] = translation.GetValue("u", BsonNull.Value),
                ["p"] = translation.Text("p"),
                ["pr"] = translation.GetValue("pr", BsonNull.Value),
                ["s"] = translation.Flag("s"),
                ["sr"] = translation.GetValue("sr", BsonNull.Value),
                ["mt"] = translation.Flag("mt"),
                ["ct"] = translation.GetValue("ct", Now),
                ["e"] = translation.GetValue("e", Now),
                ["label_epoch"] = session.Revision
            };
            await Translations.InsertOneAsync(copy, cancellationToken: ct);
            translated.Add(targetId);
            if (copy.Flag("s")) checkedTargets.Add(targetId);
        }

        foreach (var target in targets)
        {
            var targetId = target["_id"].AsObjectId;
            if (translated.Contains(targetId))
                await AdjustCountAsync(file, session, "translated_source_count", +1, targetId, ct);
            if (checkedTargets.Contains(targetId))
                await AdjustCountAsync(file, session, "checked_source_count", +1, targetId, ct);
        }

        var oldTips = await Tips.Find(new BsonDocument("o", oldSource["_id"])).ToListAsync(ct);
        foreach (var tip in oldTips)
        {
            await Tips.InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["o"] = source["_id"],
                ["t"] = tip.GetValue("t", BsonNull.Value),
                ["c"] = tip.Text("c"),
                ["u"] = tip.GetValue("u", BsonNull.Value),
                ["ct"] = tip.GetValue("ct", Now),
                ["et"] = tip.GetValue("et", Now),
                ["label_epoch"] = session.Revision
            }, cancellationToken: ct);
        }
    }

    /// <summary>Python <c>File.clear_all_sources()</c> (app/models/file.py:1220-1234).</summary>
    private async Task ClearSourcesAsync(BsonDocument file, LabelSession session, CancellationToken ct)
    {
        var fileId = file["_id"].AsObjectId;
        var sourceIds = await Sources.Find(new BsonDocument("f", fileId))
            .Project(new BsonDocument("_id", 1))
            .ToListAsync(ct);
        if (sourceIds.Count > 0)
        {
            var ids = new BsonArray(sourceIds.Select(row => row["_id"]));
            var inIds = new BsonDocument("$in", ids);
            await Translations.DeleteManyAsync(new BsonDocument("o", inIds), ct);
            await Tips.DeleteManyAsync(new BsonDocument("o", inIds), ct);
            await Sources.DeleteManyAsync(new BsonDocument("_id", inIds), ct);
        }

        var project = session.Project;
        var targets = await store.Collection("target")
            .Find(new BsonDocument("t", project["_id"]))
            .ToListAsync(ct);
        foreach (var target in targets)
        {
            var targetId = target["_id"].AsObjectId;
            var cache = await TargetCaches.Find(new BsonDocument
            {
                ["f"] = fileId,
                ["t"] = targetId
            }).FirstOrDefaultAsync(ct);
            if (cache is not null)
            {
                var translated = cache.Integer("ts");
                var checkedCount = cache.Integer("cs");
                if (translated != 0)
                    await AdjustCountAsync(file, session, "translated_source_count", -(int)translated,
                        targetId, ct);
                if (checkedCount != 0)
                    await AdjustCountAsync(file, session, "checked_source_count", -(int)checkedCount,
                        targetId, ct);
            }
        }

        var sourceCount = file.Integer("sc");
        if (sourceCount != 0)
            await AdjustCountAsync(file, session, "source_count", -(int)sourceCount, null, ct);
    }

    /// <summary>
    /// Mirrors <c>File.inc_cache</c> (app/models/file.py:647-696): file and
    /// ancestors, then the project, then the target cache/target.
    /// </summary>
    private async Task AdjustCountAsync(
        BsonDocument file,
        LabelSession session,
        string cacheName,
        int step,
        ObjectId? targetId,
        CancellationToken ct)
    {
        if (step == 0) return;
        var ids = new List<ObjectId>();
        if (file.TryGetValue("a", out var ancestors) && ancestors is BsonArray array)
        {
            foreach (var value in array)
                if (value.IsObjectId) ids.Add(value.AsObjectId);
        }
        ids.Add(file["_id"].AsObjectId);

        var field = cacheName switch
        {
            "source_count" => "sc",
            "translated_source_count" => "tsc",
            "checked_source_count" => "csc",
            _ => throw new ArgumentOutOfRangeException(nameof(cacheName))
        };
        await Files.UpdateManyAsync(new BsonDocument
        {
            ["_id"] = new BsonDocument("$in", new BsonArray(ids.Distinct().Select(id => (BsonValue)id)))
        }, new BsonDocument
        {
            ["$inc"] = new BsonDocument(field, step),
            ["$set"] = new BsonDocument("label_epoch", session.Revision)
        }, cancellationToken: ct);

        if (ProjectIdOf(file) is { } projectId)
        {
            await Projects.UpdateOneAsync(new BsonDocument("_id", projectId),
                new BsonDocument("$inc", new BsonDocument(field, step)), cancellationToken: ct);
        }

        if (targetId is null) return;
        var cacheField = cacheName == "translated_source_count" ? "ts" : "cs";
        await TargetCaches.UpdateManyAsync(new BsonDocument
        {
            ["f"] = new BsonDocument("$in", new BsonArray(ids.Distinct().Select(id => (BsonValue)id))),
            ["t"] = targetId.Value
        }, new BsonDocument
        {
            ["$inc"] = new BsonDocument(cacheField, step),
            ["$set"] = new BsonDocument("label_epoch", session.Revision)
        }, cancellationToken: ct);
        await store.Collection("target").UpdateOneAsync(new BsonDocument("_id", targetId.Value),
            new BsonDocument
            {
                ["$inc"] = new BsonDocument(field, step),
                ["$set"] = new BsonDocument("label_epoch", session.Revision)
            }, cancellationToken: ct);
    }

    private async Task<ObjectId> ProjectIdOfFileAsync(ObjectId fileId, CancellationToken ct)
    {
        var file = await RequireTextFileAsync(fileId, ct);
        return ProjectIdOf(file) ?? throw new ApiError("FileNotExistError", 8001, "文件/文件夹不存在，可能已被删除");
    }

    private async Task<ObjectId?> ProjectIdOfFileOrNullAsync(ObjectId fileId, CancellationToken ct)
    {
        var file = await Files.Find(new BsonDocument("_id", fileId)).FirstOrDefaultAsync(ct);
        return file is null ? null : ProjectIdOf(file);
    }

    private async Task<BsonDocument> RequireTextFileAsync(ObjectId fileId, CancellationToken ct) =>
        await Files.Find(new BsonDocument { ["_id"] = fileId, ["t"] = FileTypeText }).FirstOrDefaultAsync(ct)
        ?? throw new ApiError("FileNotExistError", 8001, "文件/文件夹不存在，可能已被删除");

    private static ObjectId? ProjectIdOf(BsonDocument file) =>
        file.TryGetValue("p", out var value) && value.IsObjectId ? value.AsObjectId : null;

    private static bool SameRun(BsonDocument file, string runId, string field = "parse_run") =>
        runId.Length > 0 && file.Text(field) == runId;

    private static bool TryId(BsonValue value, out ObjectId id)
    {
        id = ObjectId.Empty;
        if (value.IsObjectId) { id = value.AsObjectId; return true; }
        return value.IsString && ObjectId.TryParse(value.AsString, out id);
    }

    /// <summary>
    /// Python uses <c>chardet.detect</c>. The C# port has no chardet dependency,
    /// so it follows the same effective order for the text files this task
    /// accepts: BOM first, strict UTF-8, then GB18030, then Windows-1252.
    /// Windows-1252 cannot fail for any byte, matching chardet's usual
    /// non-null guess; a BOM we cannot decode or an impossible UTF-32 sequence
    /// still raises and becomes <c>TEXT_UNKNOWN_CHARSET</c>.
    /// </summary>
    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            return new UTF32Encoding(false, true, true).GetString(bytes, 4, bytes.Length - 4);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                return Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback).GetString(bytes);
            }
            catch (ArgumentException)
            {
                return Encoding.GetEncoding(1252).GetString(bytes);
            }
        }
    }
}
