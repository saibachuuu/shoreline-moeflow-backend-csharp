using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;

namespace Shoreline.Api.Features;

/// <summary>
/// LabelPlus import compatibility. The import is split into three public hooks
/// so ProjectService can call them without taking a dependency on the job
/// implementation:
///   ValidateBeforeCreate -> parse before any project row is written;
///   InitializeAfterCreate -> persist the pending flags and enqueue one job;
///   RunImportAsync -> the idempotent job body.
/// The HTTP entry point (ProjectImportEndpoints) uses the same hooks.
/// </summary>
public sealed partial class ProjectImportService(
    MongoStore store,
    JobQueue jobs,
    LegacyTypes types,
    ProjectLabelLock labelLock,
    LabelRequestContext labelContext,
    LabelService labelService,
    TimeProvider clock)
{
    public const string JobKind = "import_from_labelplus";

    public const int Pending = 0;
    public const int Running = 1;
    public const int Succeeded = 2;
    public const int Error = 3;

    public const int ErrorUnknown = 0;
    public const int ErrorNoTarget = 1;
    public const int ErrorNoCreator = 2;
    public const int ErrorParseFailed = 3;

    public const int ApplyNone = 1;
    public const int ApplyAll = 2;

    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private IMongoCollection<BsonDocument> Targets => store.Collection("target");
    private IMongoCollection<BsonDocument> TeamMembers => store.Collection("team_member");
    private IMongoCollection<BsonDocument> ProjectMembers => store.Collection("project_member");
    private IMongoCollection<BsonDocument> Users => store.Collection("user");
    private IMongoCollection<BsonDocument> Sources => store.Collection("source");
    private IMongoCollection<BsonDocument> Checkpoints => store.Collection("project_import_checkpoint");
    private IMongoCollection<BsonDocument> Journal => store.Collection("project_import_journal");
    private IMongoCollection<BsonDocument> Jobs => store.Collection("csharp_job");
    internal LabelService LabelWriter => labelService;

    /// <summary>Test-only observation point for crash/unknown-result windows.</summary>
    internal Action<string>? TestStepHook { get; set; }

    private static ApiError ParseFailed() =>
        new("LabelplusParseFailedError", 4029, "\"翻译数据.txt\" 解析失败");

    /// <summary>Parses LabelPlus text and maps every failure to legacy 4029.</summary>
    /// <remarks>
    /// K1 reservation: when team dissolution fencing is wired, this pre-create
    /// hook must call <c>TeamLifecycleLock.EnsureWritableAsync(teamId)</c> before
    /// parsing/writing anything. It is intentionally not called yet because K1
    /// write-side wiring is a separate slice and this method has no team id.
    /// </remarks>
    public List<LabelPlusFile> ValidateBeforeCreate(string? labelplusText)
    {
        try
        {
            return LabelPlusReader.Read(labelplusText);
        }
        catch (Exception error) when (error is FormatException or IndexOutOfRangeException or ArgumentException)
        {
            throw ParseFailed();
        }
    }

    /// <summary>
    /// Persists import flags and enqueues the canonical job. Python only queues
    /// when text is non-empty, a creator exists, and there is exactly one
    /// target; callers must have run <see cref="ValidateBeforeCreate"/> first.
    /// <paramref name="requestId"/> is retained for the HTTP call site but is
    /// deliberately NOT an idempotency key: Python's
    /// <c>TeamProjectImportAPI</c> has no request-id dedup and its
    /// <c>request_id</c> only feeds audit logs. Deduplication is the durable
    /// project <c>ij</c> job id below.
    /// </summary>
    public async Task<ObjectId?> InitializeAfterCreateAsync(
        BsonDocument project,
        BsonDocument actor,
        string? labelplusText,
        string requestId,
        CancellationToken ct)
    {
        // K1 reservation: before the first write below, the final integration
        // must call TeamLifecycleLock.EnsureWritableAsync(project["t"]) while it
        // still holds the project lock. This slice only reserves the call site;
        // it must not add a second, unlocked check that gives a false guarantee.
        // Parse again defensively: this hook is public and must never enqueue an
        // unparseable legacy file.
        if (string.IsNullOrEmpty(labelplusText)) return null;
        ValidateBeforeCreate(labelplusText);

        var projectId = project["_id"].AsObjectId;
        var expectedRevision = project.GetValue("label_revision", new BsonInt64(0)).ToInt64();

        // Initialize under the same project lock as the job. A restore can move
        // label_revision while the caller still holds an old project document;
        // the lock's revision guard then aborts instead of overwriting it/is.
        await using var session = await labelLock.EnterAsync(labelContext, projectId,
            recovery: false, expectedRevision: expectedRevision, ct);
        var current = session.Project;
        var revision = current.Integer("label_revision", 0);
        if (revision != expectedRevision) return null;

        var targetCount = await Targets.CountDocumentsAsync(new BsonDocument("t", projectId), cancellationToken: ct);
        var creator = await FindCreatorAsync(projectId, ct);
        if (targetCount != 1 || creator is null) return null;

        // The payload must be rebuilt byte-for-byte on every retry: the queue
        // compares typed BSON, including Int64 vs Int32 and field order, and
        // never accepts a JSON round-trip.
        var payload = new BsonDocument
        {
            ["project_id"] = projectId,
            ["label_revision"] = new BsonInt64(revision)
        };

        ObjectId? jobId = null;
        try
        {
            // The job id is durable intent, not a result: persist it before the
            // queue write so a crash between the two can replay the same id.
            // A queued/running row for this exact generation is reused; a
            // terminal row or a different generation gets a fresh id so a
            // re-created import is not silently pinned to an old job.
            jobId = await SelectImportJobIdAsync(current, projectId, revision, ct);

            await Projects.UpdateOneAsync(new BsonDocument("_id", projectId),
                new BsonDocument("$set", new BsonDocument
                {
                    ["it"] = labelplusText,
                    ["is"] = Pending,
                    ["ip"] = 0,
                    ["ij"] = jobId.Value
                }), cancellationToken: ct);
            await Checkpoints.DeleteOneAsync(new BsonDocument("_id", projectId), ct);

            await jobs.EnqueueOnceAsync(jobId.Value, JobKind, payload, ct);
            return jobId;
        }
        catch (JobEnqueueConflictException)
        {
            // The same id is bound to a different typed payload. That is an
            // invariant/generation mismatch, never a retry: mark the import
            // blocked and keep the text for inspection.
            await Projects.UpdateOneAsync(new BsonDocument("_id", projectId),
                new BsonDocument("$set", new BsonDocument
                {
                    ["is"] = Error,
                    ["ie"] = ErrorUnknown
                }), cancellationToken: CancellationToken.None);
            throw;
        }
        catch
        {
            // An enqueue can fail after the insert was actually committed. Do
            // not mark Error in that window: the job may already be claimable
            // and would race an error flag. Only a confirmed absence of the
            // durable job makes the pending state unrecoverable here.
            var queued = jobId is { } id && await FindImportJobAsync(id, CancellationToken.None);
            if (!queued)
            {
                await Projects.UpdateOneAsync(new BsonDocument("_id", projectId),
                    new BsonDocument("$set", new BsonDocument
                    {
                        ["is"] = Error,
                        ["ie"] = ErrorUnknown
                    }), cancellationToken: CancellationToken.None);
            }
            throw;
        }
    }

    /// <summary>
    /// Job body. Holds the shared project label lock for the whole run, checks
    /// the captured revision before changing any flag, and resumes from a
    /// durable per-file/per-label checkpoint after a partial failure.
    /// </summary>
    /// <remarks>
    /// K1 reservation: the run-side call site for
    /// <c>TeamLifecycleLock.EnsureWritableAsync(project["t"])</c> belongs at the
    /// top of this method, after the project lock and before the first flag
    /// write. It is intentionally not wired in this slice.
    /// </remarks>
    public async Task RunImportAsync(BsonDocument payload, CancellationToken ct)
    {
        if (!TryObjectId(payload.GetValue("project_id", BsonNull.Value), out var projectId))
            throw new ApiError("ProjectNotExistError", 4001, "项目不存在");

        // Python's job returns silently when the project was deleted after the
        // job was queued; it must not create a lock or mutate any flags.
        var exists = await Projects.Find(new BsonDocument("_id", projectId))
            .Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
        if (exists is null) return;

        var expectedRevision = payload.GetValue("label_revision", new BsonInt64(0)).ToInt64();

        await using var session = await labelLock.EnterAsync(labelContext, projectId, recovery: false,
            expectedRevision: expectedRevision, ct);

        var project = session.Project;
        var status = (int)project.Integer("is", Pending);
        if (status == Succeeded) return; // replay guard
        if (status == Error && project.Text("it", "").Length == 0) return;

        var target = await Targets.Find(new BsonDocument("t", projectId))
            .Sort(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
        if (target is null)
        {
            await FinishAsync(projectId, Error, ErrorNoTarget, percent: null, ct);
            return;
        }
        var creator = await FindCreatorAsync(projectId, ct);
        if (creator is null)
        {
            await FinishAsync(projectId, Error, ErrorNoCreator, percent: null, ct);
            return;
        }

        await Projects.UpdateOneAsync(new BsonDocument("_id", projectId),
            new BsonDocument("$set", new BsonDocument { ["is"] = Running, ["ip"] = 0 }),
            cancellationToken: ct);

        var checkpoint = await LoadCheckpointAsync(projectId, session.Revision, ct);
        try
        {
            var files = ValidateBeforeCreate(project.Text("it", ""));
            if (files.Count == 0)
            {
                await FinishAsync(projectId, Succeeded, null, 0, ct);
                await Checkpoints.DeleteOneAsync(new BsonDocument("_id", projectId), ct);
                return;
            }

            // Old-epoch intents must never be replayed into a restored project.
            // Delete in bounded batches so a large abandoned journal cannot
            // hold the project label lease for an unbounded amount of time.
            await DeleteOldJournalBatchesAsync(projectId, session.Revision, ct);

            for (var fileIndex = checkpoint.FileIndex; fileIndex < files.Count; fileIndex++)
            {
                var parsedFile = files[fileIndex];
                var file = await EnsurePlaceholderFileAsync(project, parsedFile.FileName, ct);
                var fileId = file["_id"].AsObjectId;
                var skip = fileIndex == checkpoint.FileIndex ? checkpoint.LabelsDone : 0;
                for (var labelIndex = skip; labelIndex < parsedFile.Labels.Count; labelIndex++)
                {
                    var label = parsedFile.Labels[labelIndex];
                    var positionType = label.PositionType == LabelService.PositionIn
                        ? LabelService.PositionIn
                        : LabelService.PositionOut;
                    var key = JournalKey(projectId, session.Revision, fileIndex, labelIndex);
                    var journal = await Journal.Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct);

                    if (journal is not null && journal.GetValue("translation_done", false).ToBoolean())
                    {
                        await SaveCheckpointAsync(projectId, session.Revision, fileIndex,
                            labelIndex + 1, ct);
                        continue;
                    }

                    if (journal is null)
                    {
                        // The durable identity is allocated before the source
                        // insert and persisted in the same intent. Rank remains
                        // ordering metadata only; it must never be used to adopt
                        // an unknown source after a crash.
                        journal = new BsonDocument
                        {
                            ["_id"] = key,
                            ["project_id"] = projectId,
                            ["revision"] = new BsonInt64(session.Revision),
                            ["file_index"] = fileIndex,
                            ["label_index"] = labelIndex,
                            ["file_name"] = parsedFile.FileName,
                            ["source_rank"] = await NextSourceRankAsync(fileId, ct),
                            ["source_id"] = ObjectId.GenerateNewId(),
                            ["translation_id"] = ObjectId.GenerateNewId(),
                            ["x"] = label.X,
                            ["y"] = label.Y,
                            ["p"] = positionType,
                            ["translation_done"] = false,
                            ["created_at"] = clock.GetUtcNow().UtcDateTime
                        };
                        await Journal.ReplaceOneAsync(new BsonDocument("_id", key), journal,
                            new ReplaceOptions { IsUpsert = true }, ct);
                    }
                    else
                    {
                        EnsureJournalShape(journal, key, projectId, session.Revision,
                            fileIndex, labelIndex, parsedFile.FileName, label, positionType);
                    }

                    TestStepHook?.Invoke("after-intent");

                    var sourceId = journal["source_id"].AsObjectId;
                    var translationId = journal["translation_id"].AsObjectId;
                    var rank = (int)journal.Integer("source_rank", 0);

                    // E1 stable-id writer: insert-or-reuse by the preallocated
                    // id, then rebuild label counters from primary rows. Rank is
                    // ordering metadata only and is never used to adopt a row;
                    // a conflicting id/file/epoch/content is rejected by the
                    // helper without being claimed.
                    await LabelWriter.CreateSourceOnceAsync(
                        context: null,
                        actor: creator,
                        fileId: fileId,
                        sourceId: sourceId,
                        content: "",
                        x: label.X,
                        y: label.Y,
                        positionType: positionType,
                        sourceRank: rank,
                        expectedRevision: session.Revision,
                        ct: ct);
                    TestStepHook?.Invoke("after-source");

                    var translation = await LabelWriter.CreateTranslationOnceAsync(
                        context: null,
                        actor: creator,
                        sourceId: sourceId,
                        targetId: target["_id"].AsObjectId,
                        translationId: translationId,
                        content: label.Translation,
                        expectedRevision: session.Revision,
                        ct: ct);
                    // Empty legacy content is a true no-op in the helper, so the
                    // hook only observes a committed translation row.
                    if (translation.Outcome != TranslationWriteOutcome.Empty)
                        TestStepHook?.Invoke("after-translation");

                    await Journal.UpdateOneAsync(new BsonDocument("_id", key),
                        new BsonDocument("$set", new BsonDocument
                        {
                            ["translation_done"] = true,
                            ["updated_at"] = clock.GetUtcNow().UtcDateTime
                        }), cancellationToken: ct);
                    await SaveCheckpointAsync(projectId, session.Revision, fileIndex,
                        labelIndex + 1, ct);
                }

                // Python advances progress with the enumerate index, so the
                // first file stays at 0 and the final file is n-1/n.
                await Projects.UpdateOneAsync(new BsonDocument("_id", projectId),
                    new BsonDocument("$set", new BsonDocument
                    {
                        ["ip"] = (int)((long)fileIndex * 100 / files.Count)
                    }), cancellationToken: ct);
                await SaveCheckpointAsync(projectId, session.Revision, fileIndex + 1, 0, ct);
            }

            await FinishAsync(projectId, Succeeded, null, 0, ct);
            await Checkpoints.DeleteOneAsync(new BsonDocument("_id", projectId), ct);
        }
        catch (ProjectImportBlockedException)
        {
            // The durable intent is kept for a safe retry; this is not a
            // legacy parse failure.
            await FinishAsync(projectId, Error, ErrorUnknown, percent: null, ct);
        }
        catch (ApiError error) when (error.Status == 409)
        {
            // A revision/fencing conflict is not a legacy parse failure. Leave
            // the import flags and journal untouched so the owner can retry.
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await FinishAsync(projectId, Error, ErrorParseFailed, percent: null, ct);
        }
    }

    private async Task FinishAsync(ObjectId projectId, int status, int? errorType, int? percent, CancellationToken ct)
    {
        var set = new BsonDocument
        {
            ["is"] = status,
            ["it"] = ""
        };
        if (errorType is not null) set["ie"] = errorType.Value;
        if (percent is not null) set["ip"] = percent.Value;
        await Projects.UpdateOneAsync(new BsonDocument("_id", projectId),
            new BsonDocument("$set", set), cancellationToken: ct);
    }

    private async Task<BsonDocument?> FindCreatorAsync(ObjectId projectId, CancellationToken ct)
    {
        var member = await ProjectMembers.Find(new BsonDocument
        {
            ["project"] = projectId,
            ["status"] = "active",
            ["tags"] = "creator"
        }).FirstOrDefaultAsync(ct);
        if (member is null || !TryObjectId(member.GetValue("user", BsonNull.Value), out var userId))
            return null;
        return await Users.Find(new BsonDocument("_id", userId)).FirstOrDefaultAsync(ct);
    }

    private async Task<ImportCheckpoint> LoadCheckpointAsync(ObjectId projectId, long revision, CancellationToken ct)
    {
        var row = await Checkpoints.Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct);
        if (row is null || row.GetValue("revision", new BsonInt64(-1)).ToInt64() != revision)
        {
            if (row is not null) await Checkpoints.DeleteOneAsync(new BsonDocument("_id", projectId), ct);
            return new ImportCheckpoint(0, 0);
        }
        return new ImportCheckpoint((int)row.Integer("file_index", 0), (int)row.Integer("labels_done", 0));
    }

    private Task SaveCheckpointAsync(ObjectId projectId, long revision, int fileIndex, int labelsDone,
        CancellationToken ct) =>
        Checkpoints.ReplaceOneAsync(new BsonDocument("_id", projectId), new BsonDocument
        {
            ["_id"] = projectId,
            ["revision"] = new BsonInt64(revision),
            ["file_index"] = fileIndex,
            ["labels_done"] = labelsDone,
            ["updated_at"] = clock.GetUtcNow().UtcDateTime
        }, new ReplaceOptions { IsUpsert = true }, ct);

    private static string JournalKey(ObjectId projectId, long revision, int fileIndex, int labelIndex) =>
        $"{projectId}:{revision}:{fileIndex}:{labelIndex}";

    private static void EnsureJournalShape(BsonDocument journal, string key, ObjectId projectId,
        long revision, int fileIndex, int labelIndex, string fileName, LabelPlusLabel label,
        int positionType)
    {
        if (!journal.TryGetValue("source_id", out var sourceId) || !sourceId.IsObjectId ||
            !journal.TryGetValue("translation_id", out var translationId) || !translationId.IsObjectId)
        {
            // Journals written before ids were preallocated cannot identify a
            // source unambiguously. Do not guess from rank or content; they
            // must be resolved manually instead of being claimed here.
            throw new ProjectImportBlockedException(
                $"import intent {key} predates stable source ids and cannot be resumed safely");
        }
        var matches = journal["project_id"].AsObjectId == projectId &&
                      journal.Integer("revision", -1) == revision &&
                      (int)journal.Integer("file_index", -1) == fileIndex &&
                      (int)journal.Integer("label_index", -1) == labelIndex &&
                      journal.Text("file_name", "") == fileName &&
                      journal.GetValue("x", BsonNull.Value).ToDouble() == label.X &&
                      journal.GetValue("y", BsonNull.Value).ToDouble() == label.Y &&
                      (int)journal.Integer("p", -1) == positionType;
        if (!matches) throw new ProjectImportBlockedException(
            $"import intent {key} no longer matches its file/epoch/label and cannot be resumed safely");
    }

    /// <summary>
    /// Chooses the durable job id for one import generation. The project's
    /// <c>ij</c> field holds the previously persisted intent. It is reused only
    /// when the corresponding queue row is still pending/running for the same
    /// project and revision (a crash retry), or when no row was written yet.
    /// A different generation or a terminal row gets a fresh id.
    /// </summary>
    private async Task<ObjectId> SelectImportJobIdAsync(BsonDocument project, ObjectId projectId, long revision,
        CancellationToken ct)
    {
        if (TryObjectId(project.GetValue("ij", BsonNull.Value), out var stored))
        {
            var job = await Jobs.Find(new BsonDocument("_id", stored)).FirstOrDefaultAsync(ct);
            if (job is null) return stored;
            if (IsQueuedImportJob(job, projectId, revision)) return stored;
        }
        return ObjectId.GenerateNewId();
    }

    private bool IsQueuedImportJob(BsonDocument job, ObjectId projectId, long revision)
    {
        if (job.Text("kind") != JobKind) return false;
        var state = job.Text("state");
        if (state is not ("pending" or "running")) return false;
        if (!job.TryGetValue("payload", out var value) || value.BsonType != BsonType.Document) return false;
        var payload = value.AsBsonDocument;
        if (!TryObjectId(payload.GetValue("project_id", BsonNull.Value), out var storedProject) ||
            storedProject != projectId)
            return false;
        if (!payload.TryGetValue("label_revision", out var storedRevision) || !storedRevision.IsNumeric)
            return false;
        return storedRevision.ToInt64() == revision;
    }

    private sealed class ProjectImportBlockedException(string message) : Exception(message);

    private async Task<int> NextSourceRankAsync(ObjectId fileId, CancellationToken ct)
    {
        var last = await Sources.Find(new BsonDocument("f", fileId))
            .Sort(new BsonDocument("r", -1))
            .Limit(1)
            .FirstOrDefaultAsync(ct);
        return last is null ? 0 : (int)last.Integer("r", 0) + 1;
    }

    /// <summary>
    /// Confirms whether the durable id was actually committed. Called only
    /// after a failed enqueue; existence at any state proves the row is there.
    /// </summary>
    private async Task<bool> FindImportJobAsync(ObjectId jobId, CancellationToken ct) =>
        await Jobs.Find(new BsonDocument("_id", jobId)).AnyAsync(ct);

    private async Task DeleteOldJournalBatchesAsync(ObjectId projectId, long revision, CancellationToken ct)
    {
        const int batchSize = 200;
        var oldRevision = new BsonDocument("$ne", new BsonInt64(revision));
        while (true)
        {
            var ids = await Journal.Find(new BsonDocument
                {
                    ["project_id"] = projectId,
                    ["revision"] = oldRevision
                })
                .Project(new BsonDocument("_id", 1))
                .Limit(batchSize)
                .ToListAsync(ct);
            if (ids.Count == 0) return;
            await Journal.DeleteManyAsync(new BsonDocument
            {
                ["_id"] = new BsonDocument("$in", new BsonArray(ids.Select(row => row["_id"])))
            }, ct);
            if (ids.Count < batchSize) return;
        }
    }

    /// <summary>
    /// Validates the exported project JSON before any database write and turns
    /// it into the shape ProjectService.CreateProjectAsync already accepts.
    /// output_language becomes target_languages, default_role is resolved to a
    /// system project role id, and project_set is forced to the URL project set.
    /// </summary>
    public async Task<JsonElement> PrepareProjectJsonAsync(JsonElement source, BsonDocument team,
        BsonDocument projectSet, CancellationToken ct)
    {
        if (source.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("Invalid request");
        var node = JsonNode.Parse(source.GetRawText())?.AsObject()
                   ?? throw ApiError.Invalid("Invalid request");

        var name = RequiredString(node, "name");
        // Python len() counts code points, not UTF-16 code units.
        if (RuneLength(name) is < 1 or > 40) throw ApiError.Field("name", "长度为1到40个字符");
        var intro = RequiredString(node, "intro");
        if (RuneLength(intro) > 140) throw ApiError.Field("intro", "长度为0到140个字符");
        var allowApply = RequiredInt(node, "allow_apply_type");
        if (allowApply is not (ApplyNone or ApplyAll)) throw ApiError.Field("allow_apply_type", "值不在允许范围内");
        var checker = RequiredInt(node, "application_check_type");
        if (checker is not (1 or 2)) throw ApiError.Field("application_check_type", "值不在允许范围内");

        var roleCode = RequiredString(node, "default_role");
        var role = await types.SystemRoleAsync("project", roleCode, ct);
        if (role.Text("m_o") == "creator") throw ApiError.Field("default_role", "默认角色必须是系统角色");

        var sourceLanguage = RequiredString(node, "source_language");
        if (!await LanguageExistsAsync(sourceLanguage, ct))
            throw ApiError.Field("source_language", "语言不存在");
        var outputLanguage = RequiredString(node, "output_language");
        if (!await LanguageExistsAsync(outputLanguage, ct))
            throw ApiError.Field("output_language", "语言不存在");
        if (sourceLanguage == outputLanguage)
            throw new ApiError("TargetAndSourceLanguageSameError", 6002, "目标语言不能与源语言相同");

        // The endpoint always moves the project into the project set named by
        // the URL, exactly like app/apis/team.py:521.
        node["project_set"] = projectSet["_id"].AsObjectId.ToString();
        node["default_role"] = role["_id"].AsObjectId.ToString();
        node.Remove("output_language");
        node.Remove("labelplus_txt");
        node["target_languages"] = new JsonArray(outputLanguage);

        return JsonSerializer.SerializeToElement(node);
    }

    private async Task<bool> LanguageExistsAsync(string code, CancellationToken ct) =>
        await store.Collection("language").Find(new BsonDocument("co", code)).AnyAsync(ct);

    private static int RuneLength(string value) => value.EnumerateRunes().Count();

    private static string RequiredString(JsonObject node, string field)
    {
        if (!node.TryGetPropertyValue(field, out var value) || value is not JsonValue json ||
            !json.TryGetValue<string>(out var text))
            throw ApiError.Field(field, "required");
        return text;
    }

    private static int RequiredInt(JsonObject node, string field)
    {
        if (!node.TryGetPropertyValue(field, out var value) || value is not JsonValue json)
            throw ApiError.Field(field, "required");
        if (json.TryGetValue<int>(out var number)) return number;
        throw ApiError.Field(field, "required");
    }

    private static bool TryObjectId(BsonValue value, out ObjectId id)
    {
        if (value.IsObjectId)
        {
            id = value.AsObjectId;
            return true;
        }
        return ObjectId.TryParse(value.ToString(), out id);
    }

    private sealed record ImportCheckpoint(int FileIndex, int LabelsDone);
}
